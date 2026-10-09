# Tillgänglighetsredogörelse: POC-underlag

Umeå kommun LLM Gateway, bedömd automatiskt 2026-10-09. Detta är ett **utkast för
POC**, inte en publicerad eller godkänd kommunal redogörelse. Ansvarig verksamhet,
kontaktväg, publiceringsdatum och rapporteringsrutiner ska fyllas i före publicering.

## Ambition och verifierad omfattning

Designmålet är WCAG 2.2 AA (inklusive 24 px målstorlek, kriterium 2.5.8), den rättsliga
baslinjen enligt DOS-lagen och EN 301 549. AAA var det ursprungliga målet men gäller inte
längre; AAA-regler kontrolleras inte. Vi **påstår inte WCAG-certifiering eller full
överensstämmelse**. Automatiska kontroller kan inte avgöra alla kriterier.

Verifierat automatiskt (Playwright mot en körande stack, 34 tester): axe-regler för WCAG
2.2 A/AA på alla routade sidor och på formulärpaneler och dialoger (inklusive Routingregler)
i de tre temana ljus, mörk och Lumen; målstorlek enligt 2.5.8; omflöde vid 320 px och 200 %
text; tangentbord, skipplänk och fokus (fokus till detaljrubrik, tillbaka till utlösaren och
till första ogiltiga fält; rubriknivåer hoppar inte; alla kontroller har namn); forced-colors
och reduced-motion. Därtill komponent-axe och kontrasttester för tokenpar, samt verkliga
Keycloak-inloggningar, behörighetsroller och nyckeldialoger i webbresorna.
Språk: engelska (svenska serverfel och svenska namn visas med lang="sv").
**Ingen skärmläsare (NVDA, VoiceOver) har körts mot det nya gränssnittet**; inget i
detta dokument ska läsas som att en sådan kontroll är gjord.
Kontrasttester för tokenpar bevisar inte kontrast för varje möjlig överlagring och
tredjeparts-IdP. Inloggningsleverantören behöver egen granskning.

## Återstående manuell checklista

| Kontroll | Genomförande / status |
|---|---|
| NVDA + Firefox/Chrome, VoiceOver + Safari | Läsrubriker, landmärken, tabeller, statusregioner, dialog/fokusåtergång: **ej utfört och inte planerat** (beslut 2026-10-10, resurser saknas); en organisation som behöver kontrollen får genomföra den själv |
| Enbart tangentbord | Kompletta CRUD-, budget-, rotation- och logoutresor, ingen fokusfälla: automatiska delar finns; manuell helhetskontroll återstår |
| Fokusutseende/ej dolt fokus | Alla fokusytor och modal-/scrolllägen, WCAG 2.4.11/2.4.13: manuell kontroll återstår |
| Förstoring och textavstånd | 200/400% webbläsarzoom, 320px, 1.4.12-avstånd, inga informationsförluster: automatiskt text/reflow; verklig zoom återstår |
| Autentisering | Passkey/MFA utan kognitiva test, IdP:s hjälp och fel: operatören väljer/granskar IdP |
| Sessionstid | Varning och explicit förlängning, även med hjälpmedel och osparade formulär: webbfunktion testad; manuell kontroll återstår |
| Språk och förståelse | Klarspråk, ordlista, instruktioner/fel, begriplig konsekvensbekräftelse: användartest behövs |
| Färg/kontrast | Alla teman, verkliga statusar, hover/disabled/tooltip, Windows hög kontrast: manuell kontroll återstår |
| Pekdon/touch | Minst 24x24px (2.5.8) kontrolleras automatiskt; avstånd, alternativa sätt att aktivera, ingen drag-only-funktion (flytta regel sker med knappar) |
| Konsekvenser | Destruktiva åtgärder, kontroll före sparande, visa-en-gång-fönster och kopiering |

Kriterier om förinspelat/live ljud/video saknar tillämpning eftersom POC:n inte
innehåller sådant innehåll. Dokumentera förändrad tillämpning om multimedia tillkommer.
Kriterier som gäller innehållsskapande, läsnivå och hela autentiseringsflödet kan inte
avfärdas som ej tillämpliga bara för att automatiska verktyg saknar regler.

## Rapportera hinder och tillsyn

Före offentlig användning: ange ansvarig enhet, tillgänglig e-post/telefon/formulär,
svarsrutin, alternativt format och länk till DIGG:s anmälningsväg. **Inga fiktiva
kontaktuppgifter anges i detta utkast.** Använd kommunens befintliga helpdesk i POC-test.
Granskningens manuella resultat och åtgärdsplan ska läggas till före godkännande.

Se [WCAG 2.2](https://www.w3.org/TR/WCAG22/) och
[DIGG](https://www.digg.se/digital-tillganglighet).
