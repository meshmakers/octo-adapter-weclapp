using System.Globalization;
using Lkv.WeClapp.Core.Mapping;
using Lkv.WeClapp.Core.Model;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration for the WeClappToCk node. Reads the current WeClapp object from
/// <c>Path</c> and writes the CK-shaped document to <c>TargetPath</c>.
/// </summary>
[NodeName("WeClappToCk", 1)]
public record WeClappToCkNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>Which WeClapp entity to transform: "Article" or "Order".</summary>
    public required string Mode { get; set; }

    /// <summary>Path of the WeClapp customer object belonging to the order (mode "Order" only).</summary>
    public string CustomerPath { get; set; } = "$.customer";
}

/// <summary>
/// Transforms WeClapp objects into CK-shaped documents (custom node #2 of the ingestion
/// design): flattens orderItems, computes net unit prices, converts epoch dates, and
/// filters system records (LOADING_EQUIPMENT articles, ANONYMOUS_DEBITOR orders) by
/// ending the pipeline for the current document (next is not invoked).
/// </summary>
[NodeConfiguration(typeof(WeClappToCkNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WeClappToCkNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<WeClappToCkNodeConfiguration>();

        switch (config.Mode)
        {
            case "Article":
                if (!TransformArticle(dataContext, nodeContext, config))
                {
                    return; // system record filtered — pipeline ends for this document
                }

                break;

            case "Order":
                if (!TransformOrder(dataContext, nodeContext, config))
                {
                    return; // system record filtered — pipeline ends for this document
                }

                break;

            default:
                throw new WeClappPipelineExecutionException(
                    $"Unknown WeClappToCk mode '{config.Mode}' (expected 'Article' or 'Order')");
        }

        await next(dataContext, nodeContext);
    }

    private static bool TransformArticle(IDataContext dataContext, INodeContext nodeContext,
        WeClappToCkNodeConfiguration config)
    {
        var article = dataContext.Get<WeClappArticle>(config.Path)
                      ?? throw new WeClappPipelineExecutionException(
                          $"No WeClapp article found at path '{config.Path}'");

        if (WeClappToDilos.IsSystemArticle(article))
        {
            nodeContext.Info($"WeClappToCk: filtered system article '{article.Id}' ({article.ArticleType})");
            return false;
        }

        dataContext.Set(config.TargetPath, ToCkArticle(article), config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);
        return true;
    }

    private static bool TransformOrder(IDataContext dataContext, INodeContext nodeContext,
        WeClappToCkNodeConfiguration config)
    {
        var order = dataContext.Get<WeClappSalesOrder>(config.Path)
                    ?? throw new WeClappPipelineExecutionException(
                        $"No WeClapp sales order found at path '{config.Path}'");

        if (WeClappToDilos.IsSystemCustomer(order.CustomerNumber))
        {
            nodeContext.Info($"WeClappToCk: filtered system order '{order.Id}' (customer {order.CustomerNumber})");
            return false;
        }

        var customer = dataContext.Get<WeClappCustomer>(config.CustomerPath)
                       ?? throw new WeClappPipelineExecutionException(
                           $"No WeClapp customer found at path '{config.CustomerPath}' " +
                           $"(required for order '{order.Id}')");

        var document = new CkOrderDocument
        {
            Customer = ToCkCustomer(customer),
            Order = ToCkOrder(order),
            OrderItems = order.OrderItems.Select(i => ToCkOrderItem(i, order.Id)).ToList(),
        };

        dataContext.Set(config.TargetPath, document, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);
        return true;
    }

    private static CkArticle ToCkArticle(WeClappArticle article) => new()
    {
        ArticleNumber = article.Id,
        Name = article.Name,
        Ean = article.Ean,
    };

    private static CkCustomer ToCkCustomer(WeClappCustomer customer) => new()
    {
        CustomerNumber = customer.CustomerNumber,
        Name = customer.Company.Length > 0
            ? customer.Company
            : $"{customer.FirstName} {customer.LastName}".Trim(),
        Contact = new CkContact
        {
            CompanyName = customer.Company,
            FirstName = customer.FirstName,
            LastName = customer.LastName,
            Email = customer.Email,
            Address = customer.Addresses.Count > 0 ? ToCkAddress(customer.Addresses[0]) : null,
        },
    };

    private static CkAddress ToCkAddress(WeClappAddress address) => new()
    {
        Street = address.Street1,
        Zipcode = address.Zipcode,
        CityTown = address.City,
        NationalCode = address.CountryCode,
    };

    private static CkOrder ToCkOrder(WeClappSalesOrder order) => new()
    {
        OrderNumber = order.Id,
        ExternalOrderNumber = order.OrderNumber,
        OrderDate = FromEpoch(order.OrderDate),
        DeliveryDate = order.PlannedShippingDate is { } d ? FromEpoch(d) : null,
    };

    private static CkOrderItem ToCkOrderItem(WeClappOrderItem item, string orderId)
    {
        if (!double.TryParse(item.Quantity, NumberStyles.Float, CultureInfo.InvariantCulture, out var quantity))
        {
            throw new WeClappPipelineExecutionException(
                $"Order '{orderId}' position {item.PositionNumber}: quantity '{item.Quantity}' is not a number");
        }

        double? unitPriceNet = null;
        if (item.NetAmount is { } netRaw &&
            double.TryParse(netRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var net))
        {
            unitPriceNet = quantity == 0d ? net : net / quantity;
        }

        return new CkOrderItem
        {
            PositionNumber = item.PositionNumber,
            Quantity = quantity,
            UnitPriceNet = unitPriceNet,
            ArticleNumber = item.ArticleId,
        };
    }

    private static DateTime? FromEpoch(long epochMs) =>
        epochMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
}
