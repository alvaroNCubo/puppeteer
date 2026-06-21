# Choreography — desarrollo en Windows

Esta guía cubre cómo iterar sobre el módulo `Choreography` (especialmente el transport SimpleX) desde una máquina Windows. **Windows no es target de producción** — the host Performance corre en Android/iOS — pero es el entorno donde el dev itera.

## TL;DR

- Para tests unit + lógica de Stage/Transport/InMemory: corres normal con `dotnet test`. SimpleX en Windows hace fallback a `InMemoryTransport` (stub).
- Para validar el path SMP real desde Windows: levantás un **server SimpleX local en Docker** y corres los smoke tests integration apuntando a `localhost:5223`. Funciona con BC.Tls (no requiere emulador Android).
- Para cerrar el ciclo en Android: necesitás **emulador Android (MAUI workload)**. Mismo código corre en `net9.0-android` sin cambios.

## 1. Prerequisitos

| Tool | Versión mínima | Notas |
|---|---|---|
| .NET SDK | 9.0 | `dotnet --version` |
| Visual Studio 2022 (o JetBrains Rider) | última | Workload **.NET MAUI** instalado solo si vas a probar en Android |
| Docker Desktop | 28.x | Sólo si vas a levantar SMP server local |
| Android SDK + emulator API 34 | — | Sólo para path Android, lo trae VS Installer |
| Java OpenJDK 17 | — | Sólo para Android. `JAVA_HOME` apuntando ahí |

Variables de entorno típicas en Windows:
```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.x"
```

## 2. Modos de ejecución

### 2.1 Stub Windows (default — sin emulador, sin Docker)

Cuando `Stage.ConfigureTransport(TransportType.SimpleX, host, ...)` se llama en Windows, `Stage.cs` detecta `OperatingSystem.IsWindows()` y devuelve `InMemoryTransport`. Imprime warning:

```
[Choreography] SimpleX transport stubbed to InMemoryTransport on Windows.
Run on Android emulator or Linux for the real path.
```

**Cuándo usar**: tests unit, lógica de StageManager, replication entre dos `Stage` en el mismo proceso.

**Limitación**: el `InMemoryTransport._registry` es `static` per-process. Dos the host Performance distintos en la misma máquina Windows **no se ven**. Es deliberado — para cross-process necesitás SimpleX real.

### 2.2 SMP server local en Docker (recomendado para iterar el wire format)

```powershell
docker run -d --name smp-test-server `
  -p 5223:5223 `
  -e "ADDR=smp-test.local" `
  -e "WEB_MANUAL=1" `
  -v "$env:USERPROFILE\smp-test\config:/etc/opt/simplex" `
  -v "$env:USERPROFILE\smp-test\state:/var/opt/simplex" `
  simplexchat/smp-server:latest
```

Notas:
- `WEB_MANUAL=1` desactiva el static-site HTTPS port 443 (que falla sin certs y termina el container).
- `ADDR=smp-test.local` es required — el server rechaza `localhost` o IPs.
- Volumes persisten certs+keys entre runs; sin volumes, cada arranque genera fingerprint nuevo.

Capturar el fingerprint que el server genera:

```powershell
docker logs smp-test-server | Select-String "Fingerprint:"
# Fingerprint: TJOK_7zBu8X8235-lfXXMBwqMtNVrG8EzUaWTPNuUSI=
```

Setear como env var para los smoke tests:

```powershell
$env:LOCAL_SMP_KEYHASH = "TJOK_7zBu8X8235-lfXXMBwqMtNVrG8EzUaWTPNuUSI="
```

(Reemplazá con el hash que veas en tus logs.)

### 2.3 Emulador Android (validación final mobile)

```powershell
dotnet workload install android maui
emulator -avd Pixel_7_API_34
dotnet build -t:Run -f net9.0-android src/the host Performance.csproj
```

Adapter de TLS (`TlsAdapterStream`) y crypto (BouncyCastle.Cryptography) son **managed puros** — el mismo código corre sin cambios en Android.

### 2.4 WSL Linux (alternativa para path SMP real sin Android)

Si no querés Docker Desktop pero tenés WSL2:

```bash
# Dentro de WSL Ubuntu
sudo apt install dotnet-sdk-9.0
git clone <repo>
cd puppeteer
LOCAL_SMP_KEYHASH=... dotnet test UnitTestChoreography
```

Linux nativo no necesita stub — `OperatingSystem.IsWindows()` es false, y BC.Tls funciona directo.

## 3. Correr tests

### 3.1 Unit tests (no requieren red ni Docker)

```powershell
dotnet test UnitTestChoreography/UnitTestChoreography.csproj
# 102/102 verde esperado
```

El filtro default (Labs / FlakyInCI / Integration excluidos) viene
del `default.runsettings` en la raíz del repo, wired vía
`<RunSettingsFilePath>` en ambos `UnitTest*.csproj`. Plain
`dotnet test` lo recoge automáticamente — local, IDE, Azure.

Estos corren en CI y deben siempre pasar.

### 3.2 Smoke tests integration (requieren red real)

Marcados `[TestCategory("FlakyInCI")]` + `[TestCategory("Integration")]` + `[DoNotParallelize]`. CI los excluye; vos los corrés localmente:

```powershell
# Smoke contra server público smp11.simplex.im (no requiere Docker)
dotnet test UnitTestChoreography/UnitTestChoreography.csproj `
  --filter "FullyQualifiedName~Smp11_Handshake"

# Smokes contra server local Docker (requieren Docker corriendo + LOCAL_SMP_KEYHASH)
$env:LOCAL_SMP_KEYHASH = "..."
dotnet test UnitTestChoreography/UnitTestChoreography.csproj `
  --filter "FullyQualifiedName~LocalSmp"
```

Smoke tests cubren:

| Test | Valida |
|---|---|
| `Smp11_Handshake_And_CreateQueue_Returns_IDS` | TLS+SMP handshake + NEW + IDS contra public server |
| `LocalSmp_Ping_Returns_Pong` | Wire format básico |
| `LocalSmp_Handshake_And_CreateQueue_Returns_IDS` | NEW + IDS local |
| `LocalSmp_Key_Returns_Ok` | KEY (recipient registra sender pubkey) |
| `LocalSmp_Send_Returns_Ok` | SEND (sender envía mensaje encriptado) |
| `LocalSmp_Sub_Returns_Ok` | SUB (recipient subscribe) |
| `LocalSmp_Ack_FakeMsgId_ReturnsErrOrOk` | ACK wire format |
| `LocalSmp_Sub_Send_Receive_E2E` | Concurrent read+write — single actor self-loop |
| `LocalSmp_TwoStages_E2E_MessageDelivered` | Multi-actor — dos clients en mismo proceso |

## 4. Troubleshooting

### "SDK not found" / Android workload missing
```powershell
dotnet workload install android maui
dotnet workload restore
```

### "JNI link error: libsimplex" o similar al cargar nativos en Android
No aplica: Choreography **no usa** binarios nativos de SimpleX. Si ves esto es otra dep — revisá `Choreography.csproj`. Solo dependencia es `BouncyCastle.Cryptography` (managed).

### "Cipher not supported" / "TLS handshake failed" en Windows
Si el code path real (no stub) corre y falla con cipher: estás contra un server que sólo ofrece `TLS_CHACHA20_POLY1305_SHA256` y SChannel viejo no lo soporta. Soluciones:
- Asegurate que `OperatingSystem.IsWindows()` esté siendo respetado (stub debería activar).
- Usá .NET 9 (SChannel moderno tiene CHACHA20).
- O corré contra Linux/WSL.

### "DllNotFoundException" en Android al arrancar
Las dependencias nativas viejas (`Sodium.Core`, `libsodium`) ya no se usan. Choreography migró a BouncyCastle managed. Si aún ves esto, revisá que `Choreography.csproj` **no** tenga `<PackageReference Include="Sodium.Core" />`.

### Server local Docker se cierra apenas arranca
Causa típica: falta `WEB_MANUAL=1`, server intenta bind port 443 HTTPS sin cert y falla. Mirá logs:
```powershell
docker logs smp-test-server
```
Si ves `Error: no HTTPS credentials`, agregá `-e WEB_MANUAL=1`.

### Test cuelga 30+ minutos
Si un smoke test integration cuelga sin emitir output:
1. Probable: server no responde (red caída, server caído, keyHash incorrecto).
2. Mata `testhost.exe`:
   ```powershell
   Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
3. Verificá que `LOCAL_SMP_KEYHASH` matchea el server actual:
   ```powershell
   docker logs smp-test-server | Select-String "Fingerprint:"
   ```

### `Cannot write application data on closed/failed TLS connection`
Server cerró el TLS — keyHash incorrecto o wire format incorrecto. Revisá:
- `LOCAL_SMP_KEYHASH` está bien seteado.
- Container Docker está running: `docker ps`.
- Si server público (smp11): verificá conectividad: `Test-NetConnection -ComputerName smp11.simplex.im -Port 5223`.

## 5. Branches

- `master` — ramas estables. Contiene Path C completo (Fases 1–7).
- `wip/choreography` — preservada como reference history. Sincronizada con master.

## 6. Roadmap restante (post-Fase 7)

- ~~**Envelope flow real en `SimplexTransport`**~~ — completado. `AcceptInvitationAsync` y `WaitForConnectionAsync` ahora cablean ReverseQueueEnvelope + ForwardKeyEnvelope con pubkeys cruzadas (senderSign + senderDh). KEY simétrico aplicado en ambas queues. La overload `SecureQueueAsync(queue)` fail-fast fue removida.
- **Decoder de `ServerDhPublicKey` ASN.1 DER → raw 32B** para usar en crypto_box (server lo envía DER en IDS).
- **Double Ratchet** opcional (forward secrecy entre Stages). Diferido como decisión arquitectónica.
- **Encoder share-link HTTPS** opcional (hoy sólo decoder). Si Choreography quiere emitir invitations interop con SimpleX Chat oficial.

## 7. Referencias

- Repo: `Choreography/Transport/SimpleX/`
- Spec SMP: https://github.com/simplex-chat/simplexmq/blob/master/protocol/simplex-messaging.md
- Memory tags relacionados: `project_choreography_simplex_path_c`, `project_choreography_ushier_role`, `feedback_integration_tests_pipeline`.
