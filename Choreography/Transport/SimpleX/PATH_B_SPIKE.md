# Path B Spike — libsimplex via P/Invoke desde .NET MAUI

**Fecha**: 2026-05-05
**Autor**: Claude (asistido), validado contra repo `simplex-chat/simplex-chat` rama `stable`
**Decisión que define este spike**: ¿seguir con Opción B (FFI a libsimplex) tal como fue presentada, o reconsiderar?

---

## TL;DR — recomendación firmada

**GO-CON-CAVEATS GRANDES.** El path B sigue siendo el más sano arquitectónicamente, pero el supuesto base que asumí en el plan original ("el equipo SimpleX publica prebuilds Android/iOS para integradores") **es falso**.

`libsimplex.so` y el equivalente iOS **no se distribuyen como artefacto independiente** ni en GitHub Releases, ni en Maven Central, ni en JitPack, ni en AAR público. Se construyen internamente como parte del build del cliente oficial mediante un toolchain GHC-cross-compile-to-Android que el propio mantainer califica de *"quite complicated"* (issue [#734](https://github.com/simplex-chat/simplex-chat/issues/734), cerrado sin documentación de build).

Esto **agrega una fase devops** al plan que no estaba prevista (producir y mantener nuestras propias `.so`). El costo cambia de ~12-19 días a algo entre **20-35 días** dependiendo del path elegido para producir los binarios.

---

## Tabla de hallazgos

| Pregunta del cuestionario | Hallazgo | Fuente |
|---|---|---|
| ¿Existe prebuild Android `libsimplex.so` descargable? | **No.** Las únicas releases son APK del cliente final (`simplex-aarch64.apk`, `simplex-armv7a.apk`). | [Releases page](https://github.com/simplex-chat/simplex-chat/releases) |
| ¿Existe XCFramework iOS publicado? | **No** en GitHub Releases. iOS bindings tampoco están en `apps/multiplatform/` (sólo Android+desktop ahí). Probablemente viven en otro subdirectorio del repo, no investigado en profundidad. | [apps/multiplatform tree](https://github.com/simplex-chat/simplex-chat/tree/master/apps/multiplatform) |
| ¿Cuál es el API FFI exacta? | 22 funciones exportadas. Las core son `chat_migrate_init`, `chat_close_store`, `chat_send_cmd[_retry]`, `chat_recv_msg[_wait]`, plus parsers (`chat_parse_uri`, `chat_parse_server`, `chat_parse_markdown`), helpers (`chat_password_hash`, `chat_valid_name`, `chat_json_length`), media (`chat_encrypt_media`, `chat_decrypt_media`, `chat_encrypt_file`, `chat_decrypt_file`, `chat_write_file`, `chat_read_file`), y los Haskell runtime hooks (`hs_init`, `hs_init_with_rtsopts`). | [libsimplex.dll.def](https://github.com/simplex-chat/simplex-chat/blob/stable/libsimplex.dll.def) |
| ¿Cómo se construye la lib? | GHC cross-compile a Android via Nix flake + cabal. El cliente Android tiene `apps/multiplatform/common/src/commonMain/cpp/android/CMakeLists.txt` que **espera** las `.so` prebuilt en `libs/${ANDROID_ABI}/libsimplex.so` y `libsupport.so`. No las compila — espera que ya existan. | [flake.nix](https://github.com/simplex-chat/simplex-chat/blob/stable/flake.nix), [cabal.project](https://github.com/simplex-chat/simplex-chat/blob/stable/cabal.project) |
| ¿Hay guidance oficial para el build? | **No.** Issue [#734](https://github.com/simplex-chat/simplex-chat/issues/734) lo pidió, fue cerrado sin link a docs. Hay scripts de comunidad (`neurocyte/ghc-android`) y referencias en el flake.nix pero no documentación step-by-step para integradores. | Issue 734 |
| ¿Modelo de DB de la lib? | La lib mantiene SQLite propia (`chat_migrate_init` recibe el path). No investigado si es expuesta o caja negra. **Riesgo**: Choreography tiene su propio journal y replication; coordinar con la SQLite de SimpleX puede requerir adaptaciones. | API inferida |
| ¿Tamaño binario Android? | No confirmado empíricamente. La APK `simplex-aarch64.apk` pesa ~30-50MB total; estimo `libsimplex.so` solo en ese rango (Haskell runtime + GMP + el core). **Esto sí es relevante para the host Performance** porque infla mucho el tamaño del APK. | Inferido |
| ¿Wrappers en otros lenguajes que sirvan de referencia? | Existen los bindings oficiales Kotlin (`platform/Core.kt` con FFI a `libapp`) y Swift (no investigado). No encontré bindings públicos en Rust, Python, Go, ni C#. | [apps/multiplatform](https://github.com/simplex-chat/simplex-chat/tree/master/apps/multiplatform) |
| ¿Limitaciones conocidas? | Issue #734: build complicado, no documentado. flake.nix referencia `simplex-chat/android-support` separado. Issue #5863 (no fetched) puede tener más contexto. | — |

---

## Implicaciones para el plan original

El plan original asumió que Fase 2 (wrapper P/Invoke) sólo agregaba un binario empaquetado. **Ahora también requiere producir el binario.** Eso no es un cambio menor.

### Tres caminos viables para producir `libsimplex.so`

| Opción | Costo dev inicial | Mantenimiento | Riesgo |
|---|---|---|---|
| **B.1 Build propio con Nix flake** | Alto (~1-2 semanas devops Haskell) | Cada bump de versión re-build | Bajo: es lo "correcto", reproducible. Requiere Nix o WSL/Linux build host. |
| **B.2 Extraer `.so` de la APK oficial** | Bajo (1 día) | Cada bump de versión re-extraer | Medio: licencia AGPL del proyecto lo permite, pero queda acoplado a la versión específica de la app y sin garantía de ABI estable. |
| **B.3 Pedir al equipo SimpleX que publique los binarios** | Cero código nuestro | Dependiente de su roadmap | Alto: pueden negarse; esto puede tardar meses. |

Mi sugerencia honesta: **arrancar con B.2 (extraer de APK) como prueba de concepto** durante Fase 2-3 para validar end-to-end con código propio mínimo, y sólo si todo lo demás funciona, invertir en B.1. Si B.2 falla por algún issue de runtime (probable: `hs_init` requiere setup específico que la APK ya hace), saltar directo a B.1.

### Plan revisado en alto nivel

| Fase | Original | Revisado |
|---|---|---|
| 0 spike | 1-2 días | ✅ hecho |
| 1 stub Windows | 1 día | ✅ hecho |
| 1.5 (NUEVO) Producir `libsimplex.so` Android | — | 1-7 días según opción B.1/B.2 |
| 2 wrapper P/Invoke | 3-5 días | 3-5 días (igual) |
| 3 migración | 3-5 días | 3-5 días |
| 4 iOS (incluye XCFramework propio) | 2-4 días | 5-10 días |
| 5 limpieza | 0.5 día | 0.5 día |
| 6 tests integración | 1 día | 1 día |
| 7 doc Windows | 0.5 día | 0.5 día |
| **Total** | 12-19 días | **20-35 días** |

---

## Riesgos identificados y mitigaciones

1. **Acoplamiento de versión**: el FFI puede cambiar entre versiones de la app oficial. Mitigar fijando versión y bumping deliberado.
2. **`hs_init` setup correcto**: el Haskell runtime requiere init exacto antes de llamadas; si la APK lo hace en `Application.onCreate()` con flags específicas, hay que replicar eso desde MAUI. Investigar `Core.kt` antes de tirar P/Invoke.
3. **DB de la lib vs journal de Choreography**: dos sistemas de persistencia conviven. Hay que decidir si Choreography invoca la lib via `chat_migrate_init(path)` con path propio per-Stage, y trata la SQLite como caja negra.
4. **iOS XCFramework**: requiere build de Apple Silicon + simulator combinado. No investigado en este spike.
5. **Tamaño APK**: si `libsimplex.so` agrega ~20MB por ABI, the host Performance puede explotar el límite del Play Store o requerir App Bundle splits. Verificar antes de comprometer.

---

## Alternativas si todo lo anterior es deal-breaker

- **Opción C (nativo C# en .NET)**: vuelve a estar sobre la mesa como camino más controlable. ~800-1500 LOC pero sin devops Haskell, sin acoplamiento a builds externos. El costo de mantenimiento es proporcional al ritmo de cambio de la spec SMP, que históricamente es lento.
- **Opción D (backend proxy)**: the host Performance mobile habla con backend Linux que sí puede correr `simplex-chat` CLI o cargar `libsimplex.so` (Linux x64 prebuild existe en flake.nix). Rompe P2P pero sortea todo el problema mobile.

---

## Lo que NO investigué en este spike (declarado explícitamente)

- iOS: no fui a `apps/ios/` para mapear bindings Swift y XCFramework. Si el plan continúa, ese es el primer fetch faltante.
- Tamaño binario real: no descargué una APK para medir `libsimplex.so` real.
- Modelo de threading exacto de `chat_recv_msg`: deduzco bloqueante por el sufijo `_wait` opcional, pero no confirmado leyendo `Core.kt`.
- Schema JSON de los comandos/eventos: cada `chat_send_cmd` toma un string command y devuelve JSON; el lenguaje de comandos no fue mapeado. Es trabajo de Fase 3.
- Issue `#5863` que apareció en el search puede tener contexto adicional sobre integradores externos.

---

## Próximo paso recomendado para Alvaro

Decidir entre:

1. **Seguir con B y aceptar la fase 1.5 (producir binarios propios).** Yo recomiendo arrancar con B.2 (extraer APK) para validar rápido y migrar a B.1 si funciona.
2. **Volver a Opción C** (implementación nativa C#) ahora que el costo real de B está claro y es comparable.
3. **Combinar B + D**: the host Performance mobile usa stub de transport en dev, y un backend Linux con `simplex-chat` CLI hace el real bridging hasta que B madure. Permite probar la lógica de Choreography sin bloquear en la fase 1.5.
