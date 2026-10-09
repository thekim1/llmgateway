# Tillgänglighetsredogörelse: POC-underlag

Umeå kommun LLM Gateway, bedömd automatiskt 2026-10-07. Detta är ett **utkast för
POC**, inte en publicerad eller godkänd kommunal redogörelse. Ansvarig verksamhet,
kontaktväg, publiceringsdatum och rapporteringsrutiner ska fyllas i före publicering.

## Ambition och verifierad omfattning

Designmålet är WCAG 2.2 AAA, med AA som relevant rättslig baslinje där DOS-lagen
och EN 301 549 gäller. Vi **påstår inte WCAG AAA-certifiering eller full överensstämmelse**.
Automatiska kontroller kan inte avgöra alla kriterier.

Verifierat: 18 routade sidor i fyra teman med tillgängliga axe-regler för A/AA/AAA;
44px-kontroller; 320px omflöde/200% text; tangentbords-skipplänk och fokus;
forced-colors och reduced-motion; komponent-axe och kontrasttester för design-tokenpar.
Verkliga Keycloak-inloggningar, behörighetsroller och nyckeldialoger ingår i webbresorna.
Teman: ljus, mörk och Lumen (efter designprototypen). Språk: engelska (gränssnittet är ej längre tvåspråkigt; svenska serverfel visas med lang="sv"). Skärmläsar- och tangentbordskontroller samt axe-skanning måste göras om för det nya gränssnittet. Sidan Routingregler (`/routing-rules`) har lint (vuejs-accessibility) och komponenttester (etiketter, fokus, `aria-live`-meddelanden, sant/falskt/ej utvärderbart som text och ikon, inte bara färg) men ännu ingen axe-skanning eller manuell skärmläsarkontroll.
Kontrasttester för tokenpar bevisar inte kontrast för varje möjlig överlagring och
tredjeparts-IdP. Inloggningsleverantören behöver egen granskning.

## Återstående manuell checklista

| Kontroll | Genomförande / status |
|---|---|
| NVDA + Firefox/Chrome, VoiceOver + Safari | Läsrubriker, landmärken, tabeller, statusregioner, dialog/fokusåtergång: **ej utfört** |
| Enbart tangentbord | Kompletta CRUD-, budget-, rotation- och logoutresor, ingen fokusfälla: automatiska delar finns; manuell helhetskontroll återstår |
| Fokusutseende/ej dolt fokus | Alla fokusytor och modal-/scrolllägen, WCAG 2.4.11/2.4.13: manuell kontroll återstår |
| Förstoring och textavstånd | 200/400% webbläsarzoom, 320px, 1.4.12-avstånd, inga informationsförluster: automatiskt text/reflow; verklig zoom återstår |
| Autentisering | Passkey/MFA utan kognitiva test, IdP:s hjälp och fel: operatören väljer/granskar IdP |
| Sessionstid | Varning och explicit förlängning, även med hjälpmedel och osparade formulär: webbfunktion testad; AAA:s tidsundantag måste bedömas |
| Språk och förståelse | Klarspråk, ordlista, instruktioner/fel, begriplig konsekvensbekräftelse: användartest behövs |
| Färg/kontrast | Alla teman, verkliga statusar, hover/disabled/tooltip, Windows hög kontrast: manuell kontroll återstår |
| Pekdon/touch | 44x44px, avstånd, alternativa sätt att aktivera, ingen drag-only-funktion |
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
