# WeClapp-API-Zugang aus GlobalConfiguration (statt Inline-Key im Pipeline-YAML)

**Datum:** 2026-07-30 · **Branch:** `feature/ab4228-apikey-globalconfig` (Basis `f303ba7`) · **Anlass:** Review-Hinweis Reimar (API-Key mehrfach direkt in den Pipelines; Studio-Configuration nutzen)

## Problem

Der WeClapp-API-Key steht heute 5× als `apiKey: ${WECLAPP_API_KEY}`-Platzhalter in den Pipeline-YAMLs (`pipelines/*.yaml`); `scripts/om_setup_lkv.ps1:34-40` ersetzt beim Deploy Platzhalter **und** die BaseUrl (`REPLACE-TENANT`) durch Klartext aus lokalen Env-Vars. Folge: Der Klartext-Key liegt in jeder deployten Pipeline-Definition am Tenant (dort hat Reimar ihn gesehen), Rotation erfordert 5 Re-Deploys, und die Verbindungsdaten leben an zwei Orten (YAML + Skript-Env).

Für SFTP ist das Problem bereits gelöst: `serverConfiguration: LkvSftp` referenziert einen GlobalConfiguration-Eintrag am Tenant (`SftpConnectionSettingsResolver`, `SftpFileSystem.cs:66-88`) — inklusive Nicht-Geheimnissen wie Host und Port.

## Ziel (Endzustand nach Expand → Migrate → Contract)

```yaml
# vorher                                                    # nachher
baseUrl: https://REPLACE-TENANT.weclapp.com/webapp/api/v1   apiConfiguration: WeClappApi
apiKey: ${WECLAPP_API_KEY}
```

Ein Eintrag `WeClappApi` `{ baseUrl, apiKey }` pro Tenant (Studio: octosystem/general/configurations, Quelle Keeper), per **Uses-Association** an die 5 Pipelines gehängt. Key existiert 1× pro Tenant; `om_setup_lkv.ps1` braucht keine Substitutionen mehr; Rotation = ein Studio-Edit.

## Design (Expand-Schritt = dieser PR)

### Neue Bausteine (Muster: `SftpConnectionSettings` + Resolver, gleiche Datei-Nachbarschaft)

```csharp
public record WeClappConnectionSettings
{
    public required string BaseUrl { get; init; }   // z. B. https://<tenant>.weclapp.com/webapp/api/v1
    public required string ApiKey  { get; init; }
}

public static class WeClappConnectionSettingsResolver
{
    // Auflösungsreihenfolge (eine Methode, von allen Nodes genutzt):
    // 1. apiConfiguration gesetzt  -> GlobalConfiguration MUSS liefern (fail-loud, KEIN Fallback):
    //    - IsDefined == false -> WeClappPipelineExecutionException
    //      ("... not defined for this pipeline — link the configuration entity to the pipeline (Uses association)")
    //    - BaseUrl oder ApiKey leer -> WeClappPipelineExecutionException (halb konfigurierter Eintrag)
    // 2. apiConfiguration leer -> Inline-Werte (baseUrl + apiKey) MÜSSEN beide gesetzt sein,
    //    sonst WeClappPipelineExecutionException ("Set either 'apiConfiguration' (recommended) or inline 'baseUrl' + 'apiKey'")
    public static WeClappConnectionSettings ResolveWeClappSettings(
        this IGlobalConfiguration globalConfiguration,
        string? apiConfiguration, string? inlineBaseUrl, string? inlineApiKey);
}
```

**Bewusste Abweichung vom Plattform-Vorbild** `AnthropicAiQueryNode.ResolveApiKey` (warnt und fällt auf Inline zurück): Wir übernehmen dessen Property-Idee (Configuration-Name gewinnt), aber die **Fehler-Semantik unseres SFTP-Resolvers** — explizit konfigurierte, fehlende Configuration ist ein Deploy-Fehler, kein stiller Fallback auf einen möglicherweise veralteten Inline-Key (Fail-loud-Linie von K1/K2).

### Config-Änderungen (beide Records, identisches Muster)

| Record | vorher | nachher |
|---|---|---|
| `WeClappFetchTriggerNodeConfiguration` | `required string BaseUrl`, `required string ApiKey` | `string? BaseUrl`, `string? ApiKey`, **neu** `string? ApiConfiguration` |
| `WeClappWriteNodeConfiguration` | dito | dito |

Aufrufstellen lösen einmal pro Poll/Verarbeitung auf und verwenden nur noch `settings.BaseUrl`/`settings.ApiKey`:
- `WeClappFetchTriggerNode`: Auflösung in `FetchOnceAsync` (Zugriff via `context.GlobalConfiguration`, wie `DilosFileFetchTriggerNode.cs:146`); Header `AuthenticationToken` (:368) und URL-Bau aus den Settings.
- `WeClappArWriteNode.cs:58` / `WeClappBeWriteNode.cs:58`: `WeClappApi`-Konstruktion aus den Settings (Zugriff via ETL-Kontext, wie `DilosSftpWriteNode`).

Der aufgelöste Key wird **nie** geloggt und **nie** in den DataContext geschrieben (gleiches Verhalten wie heute; Vorbild-Doku AnthropicAiQuery: „never exposed in the data context").

### Nicht in diesem PR (bewusst)

- **Kein YAML-Flip, keine Skript-Änderung**: alle 5 YAMLs + `om_setup_lkv.ps1` bleiben unverändert und funktionieren weiter (Expand = main bleibt jederzeit deploybar, unabhängig von Tenant-Handarbeit).
- Kein Anlegen der Tenant-Einträge per Skript (Einträge entstehen manuell aus dem Keeper — Migrate-Schritt, heute mit Reimar).
- Keine Änderungen an SFTP-/DILOS-Nodes.

### Folgeschritte nach diesem PR (nicht Teil des Diffs, im PR-Body dokumentiert)

1. **Migrate:** `WeClappApi`-Eintrag auf staging-1 (+ test-2, falls weiter genutzt) anlegen + Uses-Association an die 5 Pipelines; prod-2 im Zuge von Phase 4 (dort fehlen ohnehin LkvSftp + WeClappApi).
2. **Contract (Mini-Folgecommit):** 5 YAML-Stellen auf `apiConfiguration: WeClappApi` umstellen, beide `Replace(...)`-Zeilen aus `om_setup_lkv.ps1` entfernen. Deploy nur auf Tenants mit vorhandenem Eintrag; fehlt er doch, stoppt der Resolver fail-loud mit Handlungsanweisung.

## Fehlerbehandlung (vollständige Matrix)

| Konstellation | Verhalten |
|---|---|
| `apiConfiguration` gesetzt, Eintrag fehlt | Exception mit Eintragsname + Uses-Association-Hinweis |
| `apiConfiguration` gesetzt, Eintrag unvollständig (BaseUrl **oder** ApiKey leer) | Exception mit Eintragsname + fehlendem Feld |
| `apiConfiguration` gesetzt **und** Inline-Werte gesetzt | Configuration gewinnt (dokumentiert; wie Plattform-Vorbild) |
| `apiConfiguration` leer, Inline unvollständig (nur eins von beiden) | Exception „Set either 'apiConfiguration' … or inline 'baseUrl' + 'apiKey'" |
| `apiConfiguration` leer, Inline vollständig | Verhalten wie heute (Back-Compat; bestehende YAMLs unverändert lauffähig) |

## Tests (TDD; Fake-`IGlobalConfiguration` wie in bestehenden SFTP-Node-Tests)

1. Resolver: Configuration-Pfad liefert Settings; jede Zeile der Fehler-Matrix = ein Test (5 Fälle + Happy Paths).
2. `WeClappFetchTriggerNode`: mit `ApiConfiguration` gesetzt wird der **aufgelöste** Key als `AuthenticationToken`-Header gesendet und die **aufgelöste** BaseUrl verwendet (bestehende Fake-HTTP-Infrastruktur); Inline-Pfad bleibt grün (bestehende Tests unverändert = Back-Compat-Beweis).
3. Ar/Be-Write-Nodes: `WeClappApi` wird mit aufgelösten Settings konstruiert (ein Test je Node genügt — gleiche Resolver-Route).
4. Kein Test loggt oder asserted den Klartext-Key in Log-Ausgaben (Negativ-Check im Fetch-Test).

## Verifikation

`dotnet test -c Debug` und `-c DebugL` grün (Baseline vor Implementierung: 253/253); `dotnet format` sauber. Manuelle Staging-Probe erst nach Migrate-Schritt (nicht Merge-Voraussetzung).
