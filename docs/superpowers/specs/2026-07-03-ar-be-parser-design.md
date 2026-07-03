# Design: DILOS AR/BE-Parser (Rückrichtung LKV → WeClapp), Phase-1-Erweiterung

Datum: 2026-07-03 · Status: freigegeben (Design-Gate) + Selbstkritik-Revision v2
(Naming auf meshmakers-Konvention umgestellt) · Repo: `LkvWeClapp`

## Ziel

Die Rückrichtung der LKV-Schnittstelle lesbar machen: LKV/DILOS legt `AR…TXT`
(Auftragsrückmeldung, „orders dispatched") und `BE_…txt` (Bestandsmeldung) auf den
SFTP. Diese Bibliothek parst den **Dateiinhalt** zu sauberen C#-Objekten — das
Spiegelbild der vorhandenen Writer (`DilosArticleWriter` = AS, `DilosOrderWriter` = AI).
AR/BE ist Reini-bestätigter V1-Pflicht-Scope zum Go-Live 2026-07-15.

Legende der Belege: 📘 = offizielle DILOS-Spec (`_specs/AR.md`, `_specs/BE.md`),
📁 = echte LKV-Golden-Files (`LKV-Logistics-files/TestFiles/`, NAKED Optics 02/2024),
✅ = an allen Golden-Zeilen verifiziert (awk-Feldzählung 2026-07-03: 103× K\*=14,
103× C\*=7, 326× P\*=12, 225× L\*=11 Felder; 1114× BE=6 Felder — 100 % konsistent),
🏛️ = meshmakers-Richtlinie/Vorlage (`octo-adapter-demos`, OctoMesh.wiki,
CK `Industry.Logistics`).

## Richtlinien- und Präzedenz-Abgleich (Selbstkritik-Revision 2026-07-03)

**Verbindliche Konventionen aus Gerrys Vorlage `octo-adapter-demos` 🏛️** (dorthin
wandert dieser Code in Phase 2 als meshmakers-Adapter, Repo in der meshmakers-Org):

- Code komplett **englisch**, XML-Docs auf öffentlichen Typen/Members
- `TreatWarningsAsErrors`, Nullable reference types, `LangVersion latestmajor`
- Records / Primary Constructors (C# 12+)
- **`dotnet format <sln> --verify-no-changes`** + Build + Tests als Pre-Commit-Gate
- Unit-Tests verpflichtend für neuen Code; eigene Exception-Klasse pro Adapter
  (Vorlage: `DemoPipelineExecutionException` → bestätigt `DilosParseException`)
- Commits: `AB#{workitem}: {description}` (Wiki `Development-Guidelines`) →
  dieses Feature läuft unter **AB#4228** (Feld-Mapping AS/AI/**AR/BE**)

**Naming-Entscheidung (Revision v2):** Property-Namen **englisch, am CK
`Industry.Logistics` ausgerichtet** (die CK-Attribute sind die Ziel-Landkarte dieser
Daten: `Carrier`, `TrackingNumber`, `PackagingType`, `ServiceType`, `ParcelCount`,
`DeliveredQuantity`, `OpenQuantity`, `StockStatus{Available,Blocked}` 🏛️; CK-Naming
englisch = Team-Entscheidung 2026-06-23). Der deutsche DILOS-Originalname + Feldindex
steht in jedem XML-Doc — dasselbe Muster wie in den Phase-1-Writern
(`f[2] = o.CustomerNumber; // ClientIdnummer`). Die zuvor erwogene deutsche Benennung
stützte sich auf die Billbee-Präzedenz; die ist Domänen-Beleg, kein Stil-Vorbild.

**Billbee-Connector (`../LkvLogistik`) = Domänen-Beleg, nicht Stil-Vorbild:**

- Bestätigt: Gruppierung der `C*`/`P*`/`L*`-Zeilen über `Auftragsnummer1` zur
  Kopfzeile; Downstream-Write-back konsumiert primär **Paketnummer + Spediteur**
  (`SyncOrderFeedbackCommand`: `ShippingId = pack.Paketnummer`,
  `ConvertShipmentProvider(pack.Spediteur)`)
- ⚠️ Abweichungs-Fund: Billbees `CsvBestandsmeldung.Article` erwartet **7 BE-Felder
  (mit SKU an Index 1)** — LKV-Spec 📘 und alle 1114 Golden-Zeilen 📁 haben **6**.
  BE-Layout ist offenbar kundenspezifisch variabel → unser fail-loud auf exakt 6
  meldet die Abweichung sofort; beim ersten echten BE des WeClapp-Kunden prüfen.
- Nicht übernommen: CsvHelper (dessen Fehler-Handling ist skip-and-collect =
  widerspricht fail-loud; interpretiert `"` in Werten als Quote — riskant bei
  freien Texten), `int`-Typisierung von `ClientIdnummer` (WeClapp `customerNumber`
  kann alphanumerisch sein), Datum als `string`.
- Es gibt **keine** OctoMesh-Plattform-Funktion für Pipe-/Delimited-Dateien
  (offizielle Node-Referenz 2× verifiziert, deshalb ja Custom-Node `DilosRender`) →
  manuelles Split ist der richtige Weg, konsistent zu den manuellen Writern.

## Scope

**Drin**

- `DilosArParser.Parse(string content)` → `IReadOnlyList<DilosArShipment>`
- `DilosBeParser.Parse(string content)` → `IReadOnlyList<DilosStockLine>`
- Modelle `DilosArShipment`, `DilosParcel`, `DilosArItem`, `DilosPackingLine`,
  `DilosStockLine` + Enum `DilosStockStatus`
- Gemeinsame Format-Helfer (`DilosFormat`): Komma-Dezimal, `dd.MM.yyyy`, Zeilensplit
- `DilosParseException` mit Zeilennummer
- TDD gegen **alle 8 Golden Files** als Fixtures (reines ASCII, verifiziert 2026-07-03)
- Repo-Hygiene auf Vorlagen-Stand: `TreatWarningsAsErrors`/Nullable in csproj
  prüfen/nachziehen, `dotnet format` ins Commit-Gate

**Draußen (bewusst, kommt später / andere Schicht)**

- WeClapp-Rückschreiben (`shipment` anlegen, `warehouseStock` setzen) — braucht
  echten Kunden-Account (Jürgen, unterwegs)
- Transport: SFTP-Download, `Sperr.AR`-Lockfile 📘, Datei-Löschen, ANSI-Dekodierung
  (Parser nimmt bereits dekodierten `string`; Golden Files sind reines ASCII)
- Dateinamens-Logik. Notiz: Spec 📘 sagt `BEyyyymmddHHMMsssss.txt`, real 📁 heißen
  die Dateien `BE_20240205035403463.txt` (mit Unterstrich); AR real sowohl
  `AR00006946.TXT` (lfd. Nr., wie Spec) als auch `AR20240205143134947.TXT`
  (Timestamp) — Transport-Schicht muss beide Muster akzeptieren.
- CK-Instanz-Mapping (Shipment/Parcel/ShipmentItem/Stock existieren im CK)
- Tracking-URL-Zerlegung (siehe „Bewusst roh")

## Dateiformat (verifiziert)

Pipe-getrennt, `<CR><LF>`, **Dezimal-KOMMA** 📁📘 (z. B. `2,5` — Achtung: anders als
AI/AS, die real Punkt verwenden!). Eine AR-Datei enthält **mehrere Sendungen** 📁
(bis zu 36 `K*`-Blöcke pro Datei); Reihenfolge je Sendung: `K*` dann `C*`/`P*`/`L*`.

### AR-Satzarten (Feld-Nr. 1-indiziert wie in den Writern; Feld 1 = Präfix)

**`K*` Kopfsatz — 14 Felder ✅ → `DilosArShipment`**

| # | DILOS-Feld | Property (Typ) | Beleg/Beispiel 📁 |
|---|------------|----------------|--------------------|
| 2 | Submandant | `Division` (`string`, Spec-EN „Division") | `1` |
| 3 | ClientIdnummer | `ClientId` (`string`) | `761866`, `1` |
| 4 | ClientIdnummerkunde (Rechnungsanschrift) | `InvoiceClientId` (`string`) | `400000001572890`, oft leer |
| 5 | Zone | `Zone` (`string`) | leer |
| 6 | Auftragsnummer1 (Pflicht 📘) | `OrderNumber1` (`string`) | `5905280991569` = unsere Auftragsnummer1 aus AI |
| 7 | Auftragsnummer2 | `OrderNumber2` (`string`) | `73908`, `TEST-123` (Spec sagt „nicht gefüllt", real gefüllt 📁) |
| 8 | Auftragsnummerintern (DILOS) | `DilosOrderNumber` (`string`) | `1001764615` |
| 9 | DILOS-Frachtnummer | `DilosForwardingNumber` (`string`) | `1362265` |
| 10 | Differenzen (Pflicht 📘) | `Difference` (`string` roh) | `0`=vollständig, `2`=Fehlmengen (beide 📁) |
| 11 | Datum | `ShipmentDate` (`DateOnly?`, CK „ShipmentDate") | `05.02.2024` (`dd.MM.yyyy` 📘📁) |
| 12 | Gesamtmenge | `TotalQuantity` (`decimal?`) | `3` |
| 13 | Summe Colli | `ParcelCount` (`int?`, CK „ParcelCount") | `1` |
| 14 | Summe Gewicht (kg) | `TotalWeight` (`decimal?`) | `1,5` |

**`C*` Paketsatz — 7 Felder ✅ → `DilosParcel`**

| # | DILOS-Feld | Property (Typ) | Beleg 📁 |
|---|------------|----------------|-----------|
| 2 | Auftragsnummer1 (Pflicht) | `OrderNumber1` (`string`) | muss zum aktuellen `K*` passen (Guard) |
| 3 | Spediteur (Pflicht 📘) | `Carrier` (`string` roh, CK „Carrier") | Codes `800` (=DPD 📘), `9`; Deutung = Adapter |
| 4 | Paketnummer (Pflicht 📘) | `TrackingNumber` (`string` roh, CK „TrackingNumber") | kann Carrier-**URL** sein: `http://www.mydpd.at/?f=parcel.load&p=0625…-Z,0625…-Z` 📁. **Ultracode-Korrektur 2026-07-03:** in allen 102 Golden-URLs ist es EINE Nummer **dupliziert** (`p=X,X`; 4 URL-Formen DPD/DHL/Post/UPS), nie echt verschiedene; alle 225 L\*-Nummern sind Substrings der C\*-URL → Splitter (Phase 2) muss **dedupen** |
| 5 | Verpackungsart | `PackagingType` (`string`, CK) | `Karton` |
| 6 | Serviceart | `ServiceType` (`string`, CK) | `Standard` |
| 7 | Gewicht (kg) | `Weight` (`decimal?`) | `2,5` |

**`P*` Positionssatz — 12 Felder ✅ → `DilosArItem`**

| # | DILOS-Feld | Property (Typ) | Beleg 📁 |
|---|------------|----------------|-----------|
| 2 | Auftragsnummer1 (Pflicht) | `OrderNumber1` (`string`) | Guard gegen `K*` |
| 3 | Position | `Position` (`int?`) | `1` |
| 4 | Artikelnummer | `ArticleNumber` (`string`) | **kann leer sein** 📁 (`P*\|…\|3\|\|\|…`) — vermutlich Versandkosten-/`-1`-Gegenstück; roh lassen |
| 5 | Teilezustand | `PartCondition` (`string`) | leer |
| 6–7 | Merkmal1/Merkmal2 | `Characteristic1`/`Characteristic2` (`string`) | leer (Spec 📘 sagt Zahl `0`, real leer) |
| 8 | Serialnummer | `SerialNumber` (`string`) | leer |
| 9 | Chargennummer | `BatchNumber` (`string`) | leer |
| 10 | Auftragsmenge | `OrderedQuantity` (`decimal?`) | `0`, `1` |
| 11 | Menge geliefert (Pflicht 📘) | `DeliveredQuantity` (`decimal`, fail-loud; CK) | `1` |
| 12 | Offene Menge | `OpenQuantity` (`decimal?`, CK) | **`-1` möglich** 📁 (Überlieferung) |

**`L*` Packliste — 11 Felder ✅ → `DilosPackingLine`**

| # | DILOS-Feld | Property (Typ) | Beleg 📁 |
|---|------------|----------------|-----------|
| 2 | Auftragsnummer1 / Sendungsnummer (Pflicht 📘) | `OrderNumber1` (`string`) | Golden: immer Auftragsnummer1; Guard akzeptiert nur aktuellen `K*`-Wert (fail-loud — bewusst streng, lockern falls real Sendungsnummern auftauchen; XML-Doc vermerkt die Spec-Alternative) |
| 3 | Position | `Position` (`int?`) | `1` |
| 4 | Artikelnummer (Pflicht 📘) | `ArticleNumber` (`string`) | `39287137206429` |
| 5–9 | Teilezustand/Merkmale/Serial/Charge | wie `P*` (`string`) | leer |
| 10 | Packstückmenge (Pflicht 📘) | `PackedQuantity` (`decimal`, fail-loud) | `1` |
| 11 | Paketnummer (Pflicht 📘) | `TrackingNumber` (`string` roh) | `06255052795778-Z` — **≠ C\*-Paketnummer, wenn C\* eine URL trägt** 📁 → Parser verlinkt NICHT automatisch L\*↔C\* |

### BE — 6 Felder ✅, kein Satzart-Präfix → `DilosStockLine`

| # | DILOS-Feld | Property (Typ) | Beleg 📁 |
|---|------------|----------------|-----------|
| 1 | Artikelnummer | `ArticleNumber` (`string`) | `39287037853853` |
| 2 | Merkmal1 | `Characteristic1` (`string`) | `0` |
| 3 | Merkmal2 | `Characteristic2` (`string`) | `0` |
| 4 | Lotnummer | `LotNumber` (`string`) | leer 📘 („Leer") |
| 5 | Menge (inkl. offener Auftragsmengen 📘) | `Quantity` (`decimal`, fail-loud) | `108`, `0` |
| 6 | Zustand | `Status` (`DilosStockStatus`) | `VER`→`Available`, `GES`→`Blocked` (CK-Enum-Namen 🏛️); Golden nur `VER` 📁; unbekannt → Exception |

## Architektur

Ansatz A (freigegeben): **sequenzielle Gruppierung zu Aggregaten.** Der AR-Parser
läuft zeilenweise; `K*` eröffnet ein `DilosArShipment`, `C*`/`P*`/`L*` hängen sich an
die aktuelle Sendung. Guard: `OrderNumber1` jeder Unterzeile muss zum aktuellen `K*`
passen (Billbee bestätigt die Beziehung, löst sie aber per Listen-Lookup — unsere
sequenzielle Variante entspricht der Datei-Realität und entdeckt Korruption).
Verworfen: flache Record-Listen (Gruppieren wandert in jeden Konsumenten),
Streaming-Reader (überdimensioniert für KB-Dateien), CsvHelper (s. o.).

Neue Dateien (alle in `src/Lkv.WeClapp.Core/Dilos/`, Namespace `Lkv.WeClapp.Core.Dilos`;
englische XML-Docs mit DILOS-Originalnamen, Stil `latestmajor`/nullable/required):

- `DilosArModels.cs` — `DilosArShipment` (K\*-Properties + `Parcels`/`Items`/
  `PackingLines`-Listen), `DilosParcel`, `DilosArItem`, `DilosPackingLine`
- `DilosArParser.cs` — statische Klasse, `Parse(string)`
- `DilosBeParser.cs` — statische Klasse, `Parse(string)` + `DilosStockLine` +
  `DilosStockStatus`
- `DilosFormat.cs` — interne Helfer: `SplitLines` (CRLF/LF, leere Zeilen
  überspringen), `Dec`/`OptDec`/`OptInt` (Komma-Dezimal deterministisch über ein
  festes `NumberFormatInfo` mit `,` als Dezimaltrenner — kein Raten über
  Thread-Kultur), `OptDate` (`dd.MM.yyyy` exakt, `DateOnly.TryParseExact`)
- `DilosParseException.cs` — `Exception` mit `int LineNumber`-Property (1-basiert);
  die Zeilennummer steht zusätzlich im Message-Text

## Fehlerphilosophie (fail-loud, wie Phase 1)

`DilosParseException` mit Zeilennummer bei jedem strukturellen Defekt:

1. unbekannter Satzart-Präfix in AR (weder `K*`/`C*`/`P*`/`L*`)
2. falsche Feldanzahl (exakt 14/7/12/11 bzw. 6 — an 1871 Golden-Zeilen 100 % belegt ✅;
   fängt auch die Billbee-7-Felder-BE-Variante, falls der neue Kunde sie bekommt)
3. `C*`/`P*`/`L*` vor dem ersten `K*`
4. Auftragsnummern-Mismatch: Feld 2 einer Unterzeile ≠ `OrderNumber1` des aktuellen `K*`
5. unparsebare Pflicht-Zahl (`DeliveredQuantity`, `PackedQuantity`, BE-`Quantity`)
   oder unbekannter BE-`Status`

Leere **optionale** Felder → `null` (Zahlen/Datum) bzw. `""` (Strings). Kein Raten,
keine stillen Defaults. (Skip-and-collect wie im Billbee-`BadDataErrors` ist
Adapter-Sache: die Pipeline kann pro Datei catchen; der Parser lügt nie.)

## Bewusst roh gelassene Werte (Deutung = Adapter-/Write-back-Schicht)

- `Carrier` (Code-Tabelle 📘 100–800 ist projektspezifisch; „Auswahl erfolgt bei LKV";
  Golden enthält Code `9` AUSSERHALB der Spec-Tabelle 📁 → Adapter-Tabelle muss
  Unbekanntes tolerieren)
- `TrackingNumber` (URL-Fall 📁 — eine Nummer dupliziert, s. C\*-Tabelle; Zerlegung erst,
  wenn das WeClapp-`shipment`-Write-back definiert, was es braucht — dann mit Dedupe)
- `Difference` (`0`/`2` → CK-Enum `Completeness` erst beim CK-Mapping)
- Merkmale, Serial-/Chargennummern

## Tests (xUnit, Stil wie `DilosOrderWriterTests`: echte Golden-Werte)

Fixtures: alle 5 `AR*.TXT` + 3 `BE_*.txt` nach `tests/…/Fixtures/` kopieren
(`CopyToOutputDirectory` wie bestehende Fixtures).

1. **AR00006946** (kleinste Datei, 1 Sendung): jedes Feld aller 4 Satzarten exakt
   asserten (K\*: `Difference="2"`, `TotalWeight=2.5m` aus `2,5`, `ShipmentDate`
   10.04.2024; C\*: `Carrier="9"`; P\*: `OpenQuantity=-1`; L\*: `TrackingNumber`)
2. **Multi-Sendungs-Datei** `AR20240205143134947`: 33 Sendungen; Summen
   Parcels/Items/PackingLines = 33/92/59 ✅
3. **DPD-URL-Fall**: C\*-`TrackingNumber` bleibt die komplette URL (roh)
4. **Leere P\*-`ArticleNumber`** wird `""` (kein Fehler)
5. **Alle 5 AR-Files parsen fehlerfrei durch** (Smoke über echte Vielfalt)
6. **BE**: Wert-Assertions (`Quantity=108`, `Status=Available`) + alle 3 Files
   fehlerfrei + Zeilenzahl == Nicht-Leer-Zeilen der Datei
7. **Fail-loud-Fälle** (synthetisch): falsche Feldanzahl (inkl. 7-Felder-BE!),
   `C*` vor `K*`, Auftragsnummern-Mismatch, unbekannter Präfix, `GES` parsebar ↔
   unbekannter Zustand wirft, leere Pflicht-Menge — je
   `Assert.Throws<DilosParseException>` mit Zeilennummer-Assert

## Verifikations-Gate (pro Commit, nach Vorlage 🏛️)

`dotnet format Lkv.WeClapp.sln --verify-no-changes` → `dotnet build` →
`dotnet test`; Commit-Message `AB#4228: …`.

## Abgrenzung zu Folgearbeit (nicht Teil dieses Plans)

WeClapp-Write-back (`shipment`, `warehouseStock`) inkl. Tracking-Extraktion und
Carrier→`shipmentMethod`-Mapping; Transport (SFTP/Lock/Encoding/Archivierung);
CK-Instanz-Erzeugung. Diese Spec liefert dafür die stabile Objekt-Basis.
