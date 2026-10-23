# Plan GPU — accelerarea exportului Delta

Data: 2026-10-23. Stare: MVP implementat și validat; readback-ul asincron rămâne condițional.

## Obiectiv

Accelerăm exportul prin modificări mici, măsurabile, folosind GPU-ul pentru analiza pixelilor și CPU-ul pentru compresie și deciziile de împachetare. Adăugăm un selector pentru backend-ul analizei Delta. Calitatea și regulile exportului trebuie păstrate.

Documentul urmărește implementarea analizei Delta CPU/GPU și etapele rămase opționale.

## Punctul de plecare

- FFmpeg încearcă decodare hardware pentru sursele compatibile, apoi livrează RGBA în memoria CPU.
- `GpuFrameProcessor` execută keying, rafinarea marginilor, crop și redimensionare pe Direct3D 11.
- `Process()` apelează imediat `Readback()`, cu `CopyResource` urmat de `Map` pe o singură resursă staging.
- Analiza diferențelor, căutarea patch-urilor, hash-urile, compresia PNG/WebP și MaxRects rulează pe CPU.
- Exportul evaluează compresia mai multor patch-uri și aranjamente. Creează și un atlas PNG temporar pentru comparație/verificare.
- Exportul Delta folosește JSON compact v2; playerul nativ și runtime-ul PixiJS păstrează compatibilitatea cu v1.

## Ordinea implementării

### 1. Măsurarea etapelor — prima modificare

- [x] Adăugăm timpi și contoare în diagnostice pentru decodare, procesare GPU, așteptare/readback, analiză Delta, hash-uri, compresiile de probă, împachetare, atlasul PNG temporar, scriere și verificarea finală.
- [x] Măsurăm lucrul GPU cu instrumente GPU adecvate; timpul trimiterii comenzilor nu reprezintă durata execuției lor. Separăm inițializarea și încălzirea de procesarea normală.
- [x] Înregistrăm timpul total de export, numărul apelurilor de compresie, octeții transferați, memoria maximă și dimensiunea fișierelor.
- [x] Folosim aceleași cadre și setări pentru toate comparațiile: clip mic, 1080p, mișcare rară, mișcare densă și transparență/fade. Păstrăm fixture-uri sintetice reproductibile.

**Rezultat:** identificăm etapele dominante și alegem optimizările după timpul total economisit. Nu promitem un factor de accelerare înainte de măsurare.

### 2. Îmbunătățiri CPU simple, ghidate de măsurători

- [x] Dacă variantele finale de atlas consumă mult timp, evaluăm aranjamentele independente cu un număr limitat de lucrători CPU, în limita unui buget de memorie.
- [x] Păstrăm ordinea deterministă a rezultatelor și aceleași criterii de selecție, inclusiv la egalitate.
- [x] Verificăm cache-urile și evităm compresiile identice repetate. Refolosim implementarea existentă unde este suficientă.
- [ ] Dacă scanările CPU sunt semnificative, evaluăm SIMD pentru comparații înainte de a extinde complexitatea backend-ului GPU.

**Limită:** cadrele Delta depind de starea reconstruită anterior. Nu paralelizăm arbitrar analiza cadrelor succesive. Numărul de lucrători și paralelismul intern al codec-ului trebuie coordonate.

### 3. Prima accelerare GPU: scanările pixelilor

- [x] Extragem analiza într-o interfață restrânsă, de exemplu `IDeltaAnalyzer`, cu implementare CPU de referință și implementare DirectCompute.
- [x] Mutăm într-un prim compute shader comparația pixelilor și calculul erorii vizibile; producem masca schimbărilor, limitele regiunii schimbate și indicatori pentru blocuri.
- [x] Adăugăm detectarea limitelor RGBA nenule pentru eliminarea marginilor goale și reducerile pentru verificarea erorii, dacă măsurătorile justifică includerea lor.
- [x] Folosim textura finală deja produsă de `GpuFrameProcessor`. Evităm să descărcăm cadrul și apoi să îl încărcăm din nou doar pentru analiza GPU.
- [x] Menținem pe GPU o stare reconstruită echivalentă cu cea CPU. Comparația trebuie făcută cu această stare, nu simplu cu cadrul sursă precedent. Actualizăm starea prin aceleași operații selectate de CPU.
- [x] Folosim calcule întregi echivalente pentru comparații și reduceri, cu aceleași reguli pentru alpha și RGB invizibil. Evităm diferențele de rotunjire care ar schimba selecția patch-urilor.
- [x] Citim rezultatele analizei în buffere compacte. Adaptăm masca la algoritmul CPU existent fără a rescrie inițial căutarea și împachetarea.
- [x] Păstrăm pe CPU alegerea candidaților, partiționarea adaptivă, SHA-256, compresia și MaxRects.

**Limită importantă:** codec-urile CPU și verificarea existentă au încă nevoie de pixeli RGBA în RAM. Prima versiune nu elimină automat readback-ul complet al cadrelor; măsurăm separat câștigul scanărilor și costul sincronizării suplimentare.

### 4. Selectorul pentru utilizator

- [x] Adăugăm **Analiză Delta: Auto / CPU / GPU** în setările de export și în proiectele salvate. Proiectele vechi primesc implicit Auto.
- [x] CPU folosește implementarea de referință a analizei. Keying-ul și randarea existente continuă pe GPU.
- [x] GPU selectează analiza DirectCompute când este disponibilă. La incompatibilitate sau eroare recuperabilă, revenim la CPU dintr-o stare validă și afișăm motivul; nu continuăm cu un canvas parțial actualizat.
- [x] Auto compară un eșantion mic pe ambele backend-uri, după încălzire, incluzând transferurile și sincronizarea. Alege GPU doar când avantajul depășește variația măsurătorilor și costul alegerii este justificat.
- [x] Calibrarea folosește stare separată și nu modifică rezultatul exportului. Limităm costul ei pentru clipurile scurte; în caz de incertitudine folosim CPU.
- [x] Afișăm backend-ul efectiv și îl înregistrăm în diagnostic. Selectorul nu schimbă formatul, setările de calitate sau efortul de căutare a candidaților.

### 5. Readback asincron — după MVP, dacă profilarea îl justifică

- [ ] Înlocuim staging-ul unic cu un set mic de buffere, limitat explicit în memorie, și verificăm disponibilitatea rezultatelor înainte de acces CPU.
- [ ] Suprapunem procesarea GPU a unui cadru ulterior cu lucrul CPU independent al cadrului curent, păstrând ordinea logică a analizei Delta.
- [ ] Evităm suprascrierea texturilor încă folosite de GPU, resetările de stare incorecte și creșterea nelimitată a cozii.
- [ ] Verificăm anularea, eliberarea resurselor și revenirea la CPU la pierderea dispozitivului.

## Verificări și criterii de acceptare

- [x] CPU și GPU produc aceleași măști, limite și rezultate de eroare pe fixture-urile de referință și cazurile de margine.
- [x] Reconstrucția PNG exactă păstrează fiecare octet RGBA, inclusiv RGB invizibil. Modurile aproximative respectă garanțiile existente.
- [x] Testăm alpha parțial, apariții/dispariții, clear/replace, cadre identice, dimensiuni care nu sunt multipli ai blocului, checkpoint-uri, seek și loop.
- [x] Verificarea exportului final și testele WebP rămân active. Păstrăm compatibilitatea v1/v2 și regulile de anulare/curățare.
- [x] CPU/GPU păstrează selecția deterministă și dimensiunile exportului când algoritmul și codec-ul sunt aceleași. Investigăm orice diferență de rezultat.
- [x] Raportăm timpul total, timpii pe etape, costul calibrării Auto și memoria, folosind mai multe rulări și mediana.
- [x] Validăm inițial pe RTX 4070 Ti. Verificăm capabilitățile Direct3D la rulare și păstrăm fallback CPU pentru alte dispozitive; extindem testarea pe AMD/Intel când avem hardware disponibil.
- [x] Rulăm testele native și browser, apoi construim aplicația și verificăm exportul în Player.

## În afara primei implementări

- Encoder WebP/PNG nou pe GPU sau schimbarea formatului imaginilor.
- Înlocuirea VP9 cu alt codec pentru a folosi NVENC.
- Portarea MaxRects, SHA-256 sau a întregii căutări adaptive pe GPU.
- Integrarea directă a texturilor decoderului FFmpeg cu Direct3D pentru eliminarea tuturor transferurilor intermediare.
- Reconstrucție integrală și compresie integrală pe GPU.
- Un mod complet CPU pentru întreaga aplicație sau alte controale de calitate/efort.

## Fișiere de pornire

| Fișier | Rol |
|---|---|
| `src/AnimationMaker.Core/DeltaAtlas.cs` | Analiza Delta, candidați, verificare și coordonarea exportului |
| `src/AnimationMaker.Gpu/GpuFrameProcessor.cs` | Texturi Direct3D, procesare și readback |
| `src/AnimationMaker.Gpu/Shaders/` | Shader-ele existente și viitorul shader de analiză |
| `src/AnimationMaker.Core/ExportService.cs` | Fluxul de cadre procesate |
| `src/AnimationMaker.Core/AtlasPacker.cs` | Evaluarea aranjamentelor independente |
| `src/AnimationMaker.Core/Imaging.cs` | Compresia PNG/WebP existentă |
| `src/AnimationMaker.Core/Models.cs` | Setarea backend-ului și persistență |
| `src/AnimationMaker.App/MainWindow.xaml` și `MainWindow.xaml.cs` | Selector și afișarea backend-ului |
| `tools/AnimationMaker.DeltaDiagnostics/` | Profilare și comparații reproductibile |
| `tests/AnimationMaker.Tests/` | Echivalență CPU/GPU și regresii |

## Referințe tehnice

- [Microsoft — Compute Shader Overview](https://learn.microsoft.com/en-us/windows/win32/direct3d11/direct3d-11-advanced-stages-compute-shader): calcule paralele prin DirectCompute.
- [Microsoft — Copying and accessing resource data](https://learn.microsoft.com/en-us/windows/uwp/graphics-concepts/copying-and-accessing-resource-data): sincronizare și blocaje la readback.
- [Google — cwebp](https://developers.google.com/speed/webp/docs/cwebp): efort de compresie și multithreading.
- [NVIDIA — nvImageCodec](https://github.com/NVIDIA/nvImageCodec): backend-urile disponibile; PNG/WebP folosesc backend CPU în lista publicată.
- [FFmpeg — NVENC encoders](https://www.ffmpeg.org/ffmpeg-codecs.html#NVENC-encoders): H.264, HEVC și AV1; nu reprezintă o înlocuire directă pentru VP9-ul actual.

## Reluarea lucrului

Etapele 1–4 sunt implementate, cu excepția optimizării SIMD condiționale. Profilarea a justificat oprirea anticipată a candidaților care nu mai pot câștiga și compresia paralelă a variantelor finale (maximum doi lucrători, cu buget de memorie). Selecția și fișierele rezultate rămân deterministe. SIMD nu a fost adăugat: backend-ul GPU accelerează scanările, iar codec-urile domină încă exportul complet.

Etapa 5 este amânată: pe fixture-ul video de 12 cadre, readback-ul RGBA a însumat circa 14 ms din aproximativ 4,8 s de export (sub 0,3%). Beneficiul măsurat nu justifică acum schimbarea fluxului de cadre și a duratelor de viață ale texturilor.

Auto păstrează CPU pentru mai puțin de 60 de cadre sau număr necunoscut. Pentru clipuri mai lungi, încălzește ambele backend-uri, verifică echivalența și compară trei eșantioane; include inițializarea în calculul amortizării. Fallback-ul folosește reconstrucția CPU autoritară.

În benchmark-ul Release pe RTX 4070 Ti (mediana a trei rulări), analiza + commit + verificarea erorii au avut: 1080p sparse CPU 21,57 ms / GPU 0,89 ms; 1080p dense CPU 28,52 ms / GPU 4,01 ms; 4K sparse CPU 75,40 ms / GPU 2,19 ms. Sunt timpi de analiză, fără keying și fără codec-uri. Auto a ales GPU pentru exemplele mari și CPU pentru cel mic. Fișier local: artifacts/gpu-analysis-benchmark.json.

Validarea și comenzile de reproducere sunt în VALIDATION.md și CONTRIBUTING.md. Testarea fizică suplimentară pe AMD/Intel rămâne de făcut; fallback-ul este acoperit prin teste de erori simulate.
