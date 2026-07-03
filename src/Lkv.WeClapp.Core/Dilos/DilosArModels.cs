namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// One shipment from a DILOS AR file ("Auftragsrückmeldung", orders dispatched):
/// a K* header plus its C* parcels, P* items and L* packing lines. Property names
/// align with the Industry.Logistics CK; XML docs carry the DILOS original names.
/// </summary>
public sealed record DilosArShipment
{
    /// <summary>DILOS "Submandant" (K* field 2, spec EN "Division").</summary>
    public string Division { get; init; } = "";

    /// <summary>DILOS "ClientIdnummer" (K* field 3): customer number of the goods recipient.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>DILOS "ClientIdnummerkunde" (K* field 4): customer number of the invoice address.</summary>
    public string InvoiceClientId { get; init; } = "";

    /// <summary>DILOS "Zone" (K* field 5).</summary>
    public string Zone { get; init; } = "";

    /// <summary>DILOS "Auftragsnummer1" (K* field 6): delivery note number — our AI Auftragsnummer1.</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Auftragsnummer2" (K* field 7): shop order number (filled in golden files despite spec).</summary>
    public string OrderNumber2 { get; init; } = "";

    /// <summary>DILOS "Auftragsnummerintern" (K* field 8): DILOS-internal order number.</summary>
    public string DilosOrderNumber { get; init; } = "";

    /// <summary>DILOS "DILOS-Frachtnummer" (K* field 9): DILOS forwarding number.</summary>
    public string DilosForwardingNumber { get; init; } = "";

    /// <summary>DILOS "Differenzen" (K* field 10), raw: "0" = complete, "2" = shortages (not backordered by DILOS).</summary>
    public string Difference { get; init; } = "";

    /// <summary>DILOS "Datum" (K* field 11): date the order was dispatched (dd.MM.yyyy).</summary>
    public DateOnly? ShipmentDate { get; init; }

    /// <summary>DILOS "Gesamtmenge" (K* field 12): delivered quantity (number of parts).
    /// Golden: equals the sum of ALL P* DeliveredQuantity INCLUDING the empty-ArticleNumber
    /// shipping-cost pseudo-item (103/103 shipments) — the article-only sum is typically 1 lower.</summary>
    public decimal? TotalQuantity { get; init; }

    /// <summary>DILOS "Summe Colli" (K* field 13): number of outgoing parcels/pallets.</summary>
    public int? ParcelCount { get; init; }

    /// <summary>DILOS "Summe Gewicht" (K* field 14): total weight in kg.</summary>
    public decimal? TotalWeight { get; init; }

    /// <summary>C* records belonging to this shipment.</summary>
    public List<DilosParcel> Parcels { get; init; } = new();

    /// <summary>P* records belonging to this shipment.</summary>
    public List<DilosArItem> Items { get; init; } = new();

    /// <summary>L* records belonging to this shipment.</summary>
    public List<DilosPackingLine> PackingLines { get; init; } = new();
}

/// <summary>One DILOS AR C* record (parcel of a shipment).</summary>
public sealed record DilosParcel
{
    /// <summary>DILOS "Auftragsnummer1" (C* field 2); always matches the K* header (guarded).</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Spediteur" (C* field 3), raw carrier code (e.g. "800" = DPD per spec; mapping is
    /// adapter concern). Golden codes: 9/100/200/400/800 — "9" is OUTSIDE the spec's 100–800 table,
    /// so the adapter's code table must tolerate unknown codes.</summary>
    public string Carrier { get; init; } = "";

    /// <summary>DILOS "Paketnummer" (C* field 4), raw: may be a carrier tracking URL — no splitting
    /// here. Golden: 102/102 URLs carry ONE tracking number duplicated after a comma (p=X,X style;
    /// 4 URL shapes: DPD/DHL/Post/UPS), never genuinely distinct numbers, and every L* TrackingNumber
    /// is a substring of its shipment's C* value — a later splitter must dedupe, not just split.</summary>
    public string TrackingNumber { get; init; } = "";

    /// <summary>DILOS "Verpackungsart" (C* field 5), e.g. "Karton".</summary>
    public string PackagingType { get; init; } = "";

    /// <summary>DILOS "Serviceart" (C* field 6), e.g. "Standard".</summary>
    public string ServiceType { get; init; } = "";

    /// <summary>DILOS "Gewicht" (C* field 7): weight per parcel in kg.</summary>
    public decimal? Weight { get; init; }
}

/// <summary>One DILOS AR P* record (dispatched order position).</summary>
public sealed record DilosArItem
{
    /// <summary>DILOS "Auftragsnummer1" (P* field 2); always matches the K* header (guarded).</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Position" (P* field 3). Spec is bilingually ambiguous (DE "Position des
    /// Auftrags" ≈ AI field 3 vs EN "Position on delivery note" ≈ AI field 4); golden files are
    /// always sequential 1..n with both AI fields identical, so which AI field DILOS echoes is
    /// UNVERIFIED — verify with the first WeClapp-era AR before keying write-back on it; prefer
    /// ArticleNumber (= WeClapp articleId) for matching. Name aligns with CK ShipmentItem.PositionNumber.</summary>
    public int? PositionNumber { get; init; }

    /// <summary>DILOS "Artikelnummer" (P* field 4); may be empty in golden files (shipping-cost counterpart).</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>DILOS "Teilezustand" (P* field 5).</summary>
    public string PartCondition { get; init; } = "";

    /// <summary>DILOS "Merkmal1" (P* field 6), raw.</summary>
    public string Characteristic1 { get; init; } = "";

    /// <summary>DILOS "Merkmal2" (P* field 7), raw.</summary>
    public string Characteristic2 { get; init; } = "";

    /// <summary>DILOS "Serialnummer" (P* field 8).</summary>
    public string SerialNumber { get; init; } = "";

    /// <summary>DILOS "Chargennummer" (P* field 9).</summary>
    public string BatchNumber { get; init; } = "";

    /// <summary>DILOS "Auftragsmenge" (P* field 10): quantity that should have been delivered.</summary>
    public decimal? OrderedQuantity { get; init; }

    /// <summary>DILOS "Menge geliefert" (P* field 11, mandatory): quantity dispatched, in stock unit.</summary>
    public decimal DeliveredQuantity { get; init; }

    /// <summary>DILOS "Offene Menge" (P* field 12): undelivered quantity; negative = over-delivery (golden: -1).</summary>
    public decimal? OpenQuantity { get; init; }
}

/// <summary>One DILOS AR L* record (packing list line: which article quantity is in which parcel).</summary>
public sealed record DilosPackingLine
{
    /// <summary>DILOS "Auftragsnummer1 / Sendungsnummer" (L* field 2). Golden files always carry
    /// Auftragsnummer1; the parser guards against the current K* and fails loud otherwise
    /// (deliberately strict — relax if real files ever carry forwarding numbers).</summary>
    public string OrderNumber1 { get; init; } = "";

    /// <summary>DILOS "Position" (L* field 3). Name aligns with CK ShipmentItem.PositionNumber;
    /// same bilingual spec ambiguity as the P* field — do not key write-back on it unverified.</summary>
    public int? PositionNumber { get; init; }

    /// <summary>DILOS "Artikelnummer" (L* field 4, mandatory per spec).</summary>
    public string ArticleNumber { get; init; } = "";

    /// <summary>DILOS "Teilezustand" (L* field 5).</summary>
    public string PartCondition { get; init; } = "";

    /// <summary>DILOS "Merkmal1" (L* field 6), raw.</summary>
    public string Characteristic1 { get; init; } = "";

    /// <summary>DILOS "Merkmal2" (L* field 7), raw.</summary>
    public string Characteristic2 { get; init; } = "";

    /// <summary>DILOS "Serialnummer" (L* field 8).</summary>
    public string SerialNumber { get; init; } = "";

    /// <summary>DILOS "Chargennummer" (L* field 9).</summary>
    public string BatchNumber { get; init; } = "";

    /// <summary>DILOS "Packstückmenge" (L* field 10, mandatory): article quantity in this parcel.</summary>
    public decimal PackedQuantity { get; init; }

    /// <summary>DILOS "Paketnummer" (L* field 11): parcel/tracking number. NOTE: when the C* record
    /// carries a tracking URL, this bare number does NOT textually match C* (golden DPD case) —
    /// no automatic L*↔C* linking.</summary>
    public string TrackingNumber { get; init; } = "";
}
