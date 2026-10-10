# Vai drīkstu te darīt?

Hackathon MVP priekšpārbaudei pirms būvniecības vai īpašuma uzlabojuma ieceres.
Lietotājs izvēlas darbību un adresi, bet sistēma salīdzina adresi ar ielādētiem
atvērtiem telpiskajiem datiem un paskaidro, kādi nākamie soļi var būt vajadzīgi.

Šis nav atļauju izsniegšanas pakalpojums. Rezultāts vienmēr ir
**priekšpārbaude**, un galīgo lēmumu pieņem kompetentā iestāde.

## Arhitektūra

- `web` — React, TypeScript un Vite vienas lapas lietotne;
- `api` — .NET 10 Minimal API/BFF ar OpenAPI;
- `postgis` — telpisko datu un priekšpārbaužu audita datubāze;
- `geo-importer` — GDAL konteiners Shape, GeoPackage un GeoJSON slāņiem;
- `data-importer` — .NET 10 importētājs normalizētiem datiem un telpiskajiem
  flagiem;
- `RuleCatalog` — deterministisks, versēts noteikumu katalogs bez LLM/RAG
  lēmuma ceļā.

Interfeisā ir trīs soļi: adrese un darbība, pārbaudes rezultāts, kopsavilkums.
Priekšgals tieši nepiekļūst reģistriem — visi dati tiek vaicāti caur API.

## Ātrais starts

Lai palaistu reproducējamu demonstrāciju ar trim sagatavotiem scenārijiem:

```bash
cd building-precheck/infra
docker compose up --build
```

Pēc palaišanas:

- lietotne: `http://localhost:8081`;
- API: `http://localhost:8080`;
- OpenAPI: `http://localhost:8080/openapi/v1.json`.

Demo režīms ielādē tikai trīs adreses. Tas ir piemērots prezentācijas plūsmai,
bet ne reālu adrešu meklēšanai.

## Ieteicamais hackathon režīms

Reģionālais profils ir ātrākais režīms reālu adrešu demonstrācijai. Tas ielādē
adreses Rīgā, Jūrmalā, Liepājā, Daugavpilī un Valmierā, DAP aizsargājamās dabas
teritorijas, kā arī Rīgas plānojuma, aizsargjoslu un sarkano līniju slāņus.

Tam lokāli jābūt abām Git-ignorētajām datu mapēm:

- `.local-data/sources/` — VZD, DAP, NKMP un Rīgas telpiskie avoti;
- `new_data/` — normatīvo aktu momentuzņēmumi un papildu Rīgas atvērtie dati.

```bash
cd building-precheck/infra
docker compose \
  -f docker-compose.full.yml \
  -f docker-compose.regional.yml \
  up --build
```

Reģionālajā profilā apzināti netiek ielādēti nacionālie kadastra arhīvi un
apgrūtinājumu CSV. Tāpēc tajā nav ar kadastru sasaistītā NKMP pieminekļu slāņa,
zemes vienību ģeometrijas un pilnīgu apgrūtinājumu. Šis ierobežojums tiek
saglabāts `source_catalog` un nav paslēpts no rezultāta interpretācijas.

Lai mainītu pilsētu kopu, pirms komandas norādiet fragmentus:

```bash
ADDRESS_SCOPE_TERMS='Rīga,;Cēsis,;Sigulda,' docker compose \
  -f docker-compose.full.yml \
  -f docker-compose.regional.yml \
  up --build
```

## Pilnais nacionālais imports

Pilnajam importam izmanto tikai `docker-compose.full.yml`:

```bash
cd building-precheck/infra
docker compose -f docker-compose.full.yml up --build
```

Tas ielādē VZD adrešu reģistru, kadastra zemes vienību un zemes vienību daļu
ģeometrijas, apgrūtinājumus, NKMP sarakstu, DAP ĪADT un Rīgas plānojuma slāņus.
Imports ir ievērojami lēnāks un prasa apmēram 2 GB lokālu avotu datu.

`CADASTRE_ARCHIVE_PATTERN` ļauj nacionālajā profilā atlasīt kadastra arhīvus,
bet `IMPORT_PARCELS=false` saglabā jau esošu zemes vienību importu. Šīs opcijas
ir paredzētas lokālai izstrādei; juridiskam vai telpiski pilnīgam rezultātam
izmantojiet pilno importu.

## Noteikumu dzinējs

`src/VaiDrikstu.Domain/RuleCatalog.cs` satur pārskatāmu MVP noteikumu katalogu.
Katram noteikumam ir versija, spēkā stāšanās datums, jurisdikcija, telpiskais
nosacījums, rezultāta statuss, nākamie soļi un oficiālo avotu citāti ar
SHA-256 momentuzņēmumu.

API startā katalogs tiek reģistrēts PostGIS tabulās:

- `legal_sources`, `legal_source_versions`;
- `rules`, `rule_conditions`, `rule_effects`, `rule_citations`;
- `spatial_flags`;
- `precheck_runs`.

Katrs pieprasījums saglabā adresi, projekta parametrus, pārbaudes datumu,
atrastos telpiskos faktus, piemērotos noteikumus un pilnu atbildi. Detalizētāks
skaidrojums ir [rule-engine-without-ai.md](rule-engine-without-ai.md).

| Lietotāja līmenis | Noteikumu statuss | Nozīme |
| --- | --- | --- |
| `green` | `no_known_blocker` | Ielādētajos datos izvēlētajai darbībai nav atrasts zināms ierobežojums. Tā nav atļauja. |
| `yellow` | `conditions_apply` | Jānoskaidro DAP vai citas iestādes nosacījumi. |
| `yellow` | `manual_review_required` | Nepieciešama manuāla mantojuma, aizsargājamās teritorijas vai sarkanās līnijas pārbaude. |
| `red` | `insufficient_data` | Adresi nevarēja atrast vai ar datiem nepietiek telpiskai pārbaudei. |

Pašlaik noteikumi izmanto kultūras mantojuma, ĪADT, Rīgas kultūrvēsturisko
teritoriju, aizsargājamas apbūves, piekrastes, mikroliegumu, applūšanas riska
un sarkano līniju flagus. Rīgas funkcionālais zonējums tiek parādīts kā atradums,
bet vēl neveido automātisku atļaujas lēmumu — tam būtu vajadzīgi juridiski
pārbaudīti parametru noteikumi un pilnīga lokālplānojumu normalizācija.

## API

- `GET /api/session/mock` — demonstrācijas Latvija.gov.lv sesija;
- `GET /api/intents` — atbalstītās darbības;
- `GET /api/addresses/search?q=...` — adreses meklēšana;
- `POST /api/precheck` — priekšpārbaude;
- `GET /api/precheck/{requestId}/summary` — saglabātais kopsavilkums;
- `GET /openapi/v1.json` — OpenAPI dokuments.

`POST /api/precheck` pieņem esošo UI pieprasījumu:

```json
{
  "intentId": "terrace",
  "addressId": "101093433",
  "project": {
    "placement": "yard",
    "areaSquareMeters": 12,
    "heightMeters": 3,
    "floorCount": 1,
    "changesFacade": false
  },
  "checkedOn": "2026-10-10"
}
```

`project` un `checkedOn` jau tiek saglabāti auditam. Tie kļūs par automātiska
lēmuma daļu tikai pēc tam, kad konkrētas normas būs manuāli pārbaudītas un
formalizētas noteikumos.

## Datu robežas

Avotu datu mapes ir apzināti izslēgtas no Git un Docker build context. Konteineri
tās pievieno tikai lasīšanas režīmā. `new_data/external/manifest.json` glabā
lejupielādēto failu oficiālās saites un SHA-256 vērtības.

TAPIS un plūdu WMS/XML metadati pašlaik ir `reference-only`: piegādātie faili
nav pietiekami pilnīgi vektora dati deterministiskai telpiskai pārbaudei.

Nav automatizēti visi lokālplānojumi, detālplānojumi, inženierkomunikāciju
aizsargjoslas, precīza ieceres ģeometrija, kopīpašnieku piekrišana un slēgtie
reģistri. Tāpēc `green` nozīmē tikai to, ka ielādētajos datos nav atrasts
zināms signāls, nevis to, ka būvniecība ir atļauta.

## Pārbaudes

```bash
dotnet test VaiDrikstu.sln
pnpm --dir web build
```

Docker Compose ir ieteicamais palaišanas veids. Aspire AppHost paliek risinājumā
kā servisu izkārtojuma atsauce, bet šim datu smagajam MVP tas nav primārais
orķestrators.
