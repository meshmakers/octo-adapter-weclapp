using Lkv.WeClapp.Core.Dilos;
using Lkv.WeClapp.Core.WriteBack;

namespace Lkv.WeClapp.Core.Tests;

public class ArShipmentWritePlannerTests
{
    private const long Epoch20240206 = 1707177600000L; // 2024-02-06 00:00 UTC

    private static DilosArShipment GoldenShapedShipment() => new()
    {
        OrderNumber1 = "5910986621265",
        ShipmentDate = new DateOnly(2024, 2, 6),
        TotalQuantity = 2m,
        ParcelCount = 1,
        TotalWeight = 0.5m,
        Parcels =
        {
            new DilosParcel
            {
                OrderNumber1 = "5910986621265",
                Carrier = "800",
                TrackingNumber = "http://www.mydpd.at/?f=parcel.load&p=06255052795778-Z,06255052795778-Z",
                Weight = 0.5m
            }
        },
        Items =
        {
            new DilosArItem
            {
                OrderNumber1 = "5910986621265", PositionNumber = 1,
                ArticleNumber = "43222003744925", OrderedQuantity = 1m, DeliveredQuantity = 1m, OpenQuantity = 0m
            },
            // shipping-cost pseudo item: empty ArticleNumber, qty 1 (golden shape)
            new DilosArItem
            {
                OrderNumber1 = "5910986621265", PositionNumber = 3,
                ArticleNumber = "", DeliveredQuantity = 1m
            }
        }
    };

    [Fact]
    public void Plan_NoExistingShipment_CreatesThenUpdatesWithGoldenFields()
    {
        var plan = ArShipmentWritePlanner.Plan(GoldenShapedShipment(), []);

        Assert.Equal(ArWriteAction.CreateThenUpdate, plan.Action);
        Assert.NotNull(plan.Update);
        var u = plan.Update!;
        Assert.Equal("06255052795778-Z", u.PackageTrackingNumber);
        Assert.StartsWith("http://www.mydpd.at/", u.PackageTrackingUrl);
        Assert.Equal(Epoch20240206, u.ShippingDateEpochMs);
        Assert.Equal("0.5", u.TotalWeight);
        Assert.Equal("DPD", u.EcommerceShippingCarrier);
        Assert.Equal("800", u.CarrierToken);
        var parcel = Assert.Single(u.Parcels);
        Assert.Equal(1, parcel.PositionNumber);
        Assert.Equal("06255052795778-Z", parcel.TrackingId);
        Assert.Equal("0.5", parcel.Weight);
        // pseudo item excluded, real article kept
        var item = Assert.Single(u.ItemQuantities);
        Assert.Equal("43222003744925", item.ArticleId);
        Assert.Equal("1", item.Quantity);
    }

    [Fact]
    public void Plan_ExistingNonCancelledShipmentWithItems_UpdatesIt()
    {
        var existing = new WeClappShipmentSummary
        {
            Id = "777", Status = "NEW",
            ShipmentItems = { new WeClappShipmentItem { Id = "I1", ArticleId = "43222003744925" } }
        };

        var plan = ArShipmentWritePlanner.Plan(GoldenShapedShipment(), [existing]);

        Assert.Equal(ArWriteAction.UpdateExisting, plan.Action);
        Assert.Equal("777", plan.ExistingShipmentId);
    }

    [Fact]
    public void Plan_ExistingShippedWithSameTracking_SkipsAsReplay()
    {
        var existing = new WeClappShipmentSummary
        {
            Id = "777",
            Status = "SHIPPED",
            PackageTrackingNumber = "06255052795778-Z"
        };

        var plan = ArShipmentWritePlanner.Plan(GoldenShapedShipment(), [existing]);

        Assert.Equal(ArWriteAction.Skip, plan.Action);
        Assert.Contains("SHIPPED", plan.SkipReason);
    }

    [Fact]
    public void Plan_OnlyCancelledShipments_CreatesNew()
    {
        var cancelled = new WeClappShipmentSummary { Id = "1", Status = "CANCELLED" };

        var plan = ArShipmentWritePlanner.Plan(GoldenShapedShipment(), [cancelled]);

        Assert.Equal(ArWriteAction.CreateThenUpdate, plan.Action);
    }

    [Fact]
    public void Plan_EmptyShipmentButItemsWanted_CreatesInsteadOfReusing()
    {
        // Pre-order scenario (trial-proven 2026-07-09): createShipment without stock leaves an
        // ITEM-LESS shipment that can never reach SHIPPED ("Shipment has no items"), and items
        // are only derived at creation time — a SECOND createShipment next to the empty one
        // derives them once stock arrived. So an empty shipment is never reused when the AR
        // wants item quantities.
        var empty = new WeClappShipmentSummary { Id = "S-empty", Status = "NEW" };

        var plan = ArShipmentWritePlanner.Plan(GoldenShapedShipment(), [empty]);

        Assert.Equal(ArWriteAction.CreateThenUpdate, plan.Action);
    }

    [Fact]
    public void Plan_EmptyShipmentAndNoItemsWanted_ReusesIt()
    {
        var empty = new WeClappShipmentSummary { Id = "S-empty", Status = "NEW" };
        var arWithoutItems = GoldenShapedShipment() with { Items = [] };

        var plan = ArShipmentWritePlanner.Plan(arWithoutItems, [empty]);

        Assert.Equal(ArWriteAction.UpdateExisting, plan.Action);
        Assert.Equal("S-empty", plan.ExistingShipmentId);
    }

    [Fact]
    public void Plan_UnmappedCarrierToken_CarriesTokenWithoutWarning()
    {
        // Since Jürgen's 2026-07-08 answer, C* field 3 carries the carrier id as known in the
        // shop system (for WeClapp: the shippingCarrier entity id). The planner cannot judge
        // such a token — it carries it for the node to resolve against the live carrier list
        // and only maps the legacy DILOS/Billbee codes as a fallback. No warning here.
        var ar = GoldenShapedShipment() with
        {
            Parcels =
            [
                new DilosParcel
                {
                    OrderNumber1 = "5910986621265", Carrier = "123",
                    TrackingNumber = "1013408501850970172035"
                }
            ]
        };

        var plan = ArShipmentWritePlanner.Plan(ar, []);

        var u = plan.Update!;
        Assert.Equal("1013408501850970172035", u.PackageTrackingNumber);
        Assert.Null(u.PackageTrackingUrl);
        Assert.Null(u.EcommerceShippingCarrier);
        Assert.Equal("123", u.CarrierToken);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("carrier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_ShortagesAndOverDelivery_ProduceWarningsButWriteDeliveredQuantities()
    {
        var ar = GoldenShapedShipment() with
        {
            Difference = "2",
            Items =
            [
                new DilosArItem
                {
                    OrderNumber1 = "5910986621265", ArticleNumber = "A1",
                    OrderedQuantity = 0m, DeliveredQuantity = 1m, OpenQuantity = -1m
                }
            ]
        };

        var plan = ArShipmentWritePlanner.Plan(ar, []);

        Assert.Equal("1", Assert.Single(plan.Update!.ItemQuantities).Quantity);
        Assert.Contains(plan.Warnings, w => w.Contains("Differenzen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, w => w.Contains("over-delivery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Plan_MultipleParcels_NumbersThemSequentially()
    {
        var ar = GoldenShapedShipment() with
        {
            Parcels =
            [
                new DilosParcel { OrderNumber1 = "5910986621265", Carrier = "800", TrackingNumber = "http://x.test/?p=A,A" },
                new DilosParcel { OrderNumber1 = "5910986621265", Carrier = "800", TrackingNumber = "http://x.test/?p=B,B" }
            ]
        };

        var plan = ArShipmentWritePlanner.Plan(ar, []);

        Assert.Equal([1, 2], plan.Update!.Parcels.Select(p => p.PositionNumber));
        Assert.Equal("A", plan.Update.Parcels[0].TrackingId);
        Assert.Equal("B", plan.Update.Parcels[1].TrackingId);
        // shipment-level tracking comes from the first parcel
        Assert.Equal("A", plan.Update.PackageTrackingNumber);
    }

    [Fact]
    public void MatchItemQuantities_ByArticleId_ReportsUnmatchedBothWays()
    {
        var update = new ArShipmentUpdate
        {
            ItemQuantities =
            [
                new ArItemQuantity { ArticleId = "A1", Quantity = "1" },
                new ArItemQuantity { ArticleId = "GHOST", Quantity = "2" }
            ]
        };
        var shipmentItems = new List<WeClappShipmentItem>
        {
            new() { Id = "si1", ArticleId = "A1", Quantity = "0" },
            new() { Id = "si2", ArticleId = "UNTOUCHED", Quantity = "5" }
        };

        var result = ArShipmentWritePlanner.MatchItemQuantities(update, shipmentItems);

        var match = Assert.Single(result.Matches);
        Assert.Equal("si1", match.ShipmentItemId);
        Assert.Equal("1", match.Quantity);
        Assert.Contains(result.Warnings, w => w.Contains("GHOST"));
        Assert.Contains(result.Warnings, w => w.Contains("UNTOUCHED"));
    }

    [Fact]
    public void Plan_AllGoldenShipments_ProduceValidPlans()
    {
        var shipments = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "AR*.TXT")
            .Select(File.ReadAllText)
            .SelectMany(DilosArParser.Parse)
            .ToList();
        Assert.Equal(103, shipments.Count);

        foreach (var ar in shipments)
        {
            var plan = ArShipmentWritePlanner.Plan(ar, []);

            Assert.Equal(ArWriteAction.CreateThenUpdate, plan.Action);
            var u = plan.Update!;
            Assert.Equal(ar.Parcels.Count, u.Parcels.Count);
            Assert.False(string.IsNullOrEmpty(u.PackageTrackingNumber));
            Assert.DoesNotContain(u.ItemQuantities, i => i.ArticleId.Length == 0);
        }
    }
}
