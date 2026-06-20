using System;

namespace Puppeteer.EventSourcing
{
	// Surface read-only de inspeccion para uso CLI / IA / MCP / test-harness.
	// Separada del DSL del dominio por construccion: los verbos viven en una
	// interfaz que TODO actor expone por ser Puppeteer, no por ser Banco /
	// Tetris / etc. El DSL del dominio queda intacto.
	//
	// Implementada por ActorHandler (internal). Expuesta como
	// actor.Introspection (public) en Actor.cs — mismo patron que
	// Materialization / Reactions / Distill.
	//
	// Read-only por contrato: escribir al journal va por la superficie de
	// invocacion (Perform / Tell), nunca por aqui. Esa asimetria habilita el
	// modo shadow del CLI IA-native — la introspeccion sobre un shadow nunca
	// puede mutar el journal del primary aunque la IA lo intente.
	//
	// Etapa 1 (firmada 2026-05-31): un solo verbo ShowEntry. Range / Find /
	// Describe llegan en pasos siguientes del CLI IA-native.
	public interface IActorIntrospection
	{
		// Devuelve el record del journal con EntryId == entryId, formateado
		// como Toon (Token-Oriented Object Notation). Si no existe, lanza
		// LanguageException con un mensaje diagnostico.
		//
		// Forma del Toon (tentativa, sujeta a refinamiento por feedback):
		//
		//   id: <long>
		//   kind: "script" | "invocation" | "define"
		//   at: <DateTime>
		//   <campos especificos del kind>
		//
		// Script:     script
		// Invocation: actionId, arguments
		// Define:     actionId, define
		//
		// exposeData aparece solo cuando esta presente en el record.
		string ShowEntry(long entryId);

		// Devuelve la Define entry vigente para un actionId, formateada como
		// Toon. La invocation entry solo registra actionId — para saber QUE es
		// esa accion (su firma DSL) la IA consulta esta superficie.
		//
		// Politica de redefiniciones: si el journal contiene multiples Define
		// entries para el mismo actionId (caso atipico — re-declaracion durante
		// desarrollo), gana el de mayor EntryId. La asimetria es deliberada:
		// la version vigente es la observada por el codigo en ejecucion ahora.
		//
		// Forma del Toon:
		//
		//   actionId: <int>
		//   defineEntryId: <long>     # donde se declaro (apto para show entry)
		//   at: <DateTime>
		//   define: "<DSL canonico de la accion>"
		//
		// Si no existe ningun Define para ese actionId, lanza LanguageException.
		string ShowAction(int actionId);

		// Devuelve el set de globales actualmente vivos en la tabla de simbolos del
		// actor, formateado como Toon. Resuelve el problema de "como sabe la IA que
		// 'cia' ya existe en este actor": antes de definir cualquier singleton la
		// IA consulta este verbo y se entera de lo que el dominio ya puso.
		//
		// El symbol table contiene SOLO globales por construccion del interprete —
		// locales de bloque { ... } y parametros de actions no llegan a la tabla,
		// viven en un cache transitorio aparte (SymbolTable.cacheVariables).
		//
		// Forma del Toon:
		//
		//   symbols:
		//     - name: "cia"
		//       staticType: "Compania"
		//       runtimeType: "Compania"
		//     - name: "pago"
		//       staticType: "Pago"
		//       runtimeType: "PagoEfectivo"
		//
		// staticType: el tipo registrado en la tabla — la upper-bound polimorfica
		//   elegida en la primera asignacion. Util para validar llamadas estaticamente.
		// runtimeType: value?.GetType() — el tipo real del valor en este momento.
		//   Puede ser una subclase del staticType cuando hay polimorfismo activo.
		// Si la tabla esta vacia: 'symbols: []'.
		string ShowSymbols();

		// Devuelve detalle de un solo simbolo por nombre (case-insensitive). Si no
		// existe, lanza LanguageException.
		//
		// Forma del Toon:
		//
		//   name: "cia"
		//   staticType: "Compania"
		//   runtimeType: "Compania"
		//   value: "Compania('Pruebas')"     # solo si la clase tiene ToString sobreescrito
		//
		// El campo `value` aparece SOLO cuando la clase del runtimeType sobreescribe
		// ToString(); el default object.ToString() = FullName del tipo seria redundante
		// con runtimeType y se omite. El path legacy print(StringBuilder) NO se respeta
		// aqui — esa salida es JSON-shaped y rompe el contrato del Toon; el dominio
		// que quiera representacion inspectionable debe sobreescribir ToString.
		string ShowSymbol(string name);

		// Devuelve constructores, interfaces y metodos accesibles desde DSL de una
		// clase loaded en las LibraryAssemblies del actor. Resuelve "que puedo hacer
		// con un tipo X" — la IA encadena: show symbols -> ve cia: Compania ->
		// show class Compania -> ve los metodos invocables.
		//
		// Match case-insensitive contra Type.Name (consistente con la resolucion de
		// clases en el resto del parser de Puppeteer). Si la clase no esta en
		// ninguna library cargada, lanza LanguageException.
		//
		// Forma del Toon:
		//
		//   class: "Perro"
		//   constructors:
		//     - signature: "Perro(String)"
		//   interfaces:
		//     - name: "IAnimable"
		//   fields:
		//     - signature: "VecesLadrado : Int32"
		//       declaredOn: "Perro"
		//     - signature: "ColorOjos : String"
		//       declaredOn: "Animal"
		//   properties:
		//     - signature: "Nombre : String { get; }"
		//       declaredOn: "Perro"
		//     - signature: "Adulto : Boolean { get; set; }"
		//       declaredOn: "Perro"
		//   methods:
		//     - signature: "Ladrar() -> Void"
		//       declaredOn: "Perro"
		//     - signature: "GetEspecie() -> String"
		//       declaredOn: "Animal"
		//
		// Filtros aplicados (uniformes para metodos, fields y properties):
		//   - Visibility: public + internal + protected-internal (alineado con
		//     ParserValidation del interprete). Excluye private y pure protected.
		//   - IsSpecialName en metodos: excluye get_/set_ de properties + operator
		//     overloads (las properties ya tienen su propia coleccion).
		//   - DeclaringType == typeof(object): excluye los 4 metodos heredados base.
		//   - CompilerGenerated en fields: excluye backing fields de auto-properties
		//     (esos detalles del compilador no son parte de la API del dominio).
		//
		// Inheritance: GetFields/GetProperties/GetMethods sin DeclaredOnly incluyen
		// heredados. El campo declaredOn hace explicito de donde viene cada uno.
		//
		// Properties: incluida si AL MENOS UN accessor (get o set) es callable. El
		// signature emite solo los accessors callable — una property con setter
		// privado se muestra como '{ get; }'. Una con ambos como '{ get; set; }'.
		//
		// Fields readonly: prefijo 'readonly' en el signature
		// ('readonly NombreOriginal : String').
		//
		// Generics: types como IEnumerable&lt;CarritoItem&gt; se formatean legible
		// ('IEnumerable&lt;CarritoItem&gt;'), no con el sufijo CLR (`1[CarritoItem]).
		//
		// Interfaces: incluye transitivas — todas las interfaces que el tipo
		// satisface, no solo las declaradas directamente en la clase.
		string ShowClass(string className);

		// Devuelve TODAS las reactions registradas en el actor, con su MatchCount
		// agregado y los contadores per-Seek (entered/matched + checkpoint
		// detected/confirmed). Resuelve "que reactions tiene este actor y como van"
		// sin obligar a la IA a paginar los counters uno por uno.
		//
		// Forma del Toon:
		//
		//   reactions:
		//     - name: "ReplicaDeMoneda"
		//       matchCount: 17
		//       seeks:
		//         - name: "CreacionMoneda"
		//           entered: 17
		//           matched: 17
		//           detected: 142
		//           confirmed: 142
		//
		// Detected/confirmed vienen de DiaryStorage.GetReactionCheckpoint(reactionId,
		// level). Si la reaction nunca se ejecuto (reactionId == long.MinValue), no
		// hay checkpoint todavia y se reportan ambos en 0 — consistente con lo que
		// GetReactionCheckpoint retorna para reactionIds desconocidos.
		// Si no hay reactions: 'reactions: []'.
		string ShowReactions();

		// Devuelve detalle de UNA reaction, mas extensa que el item correspondiente
		// en ShowReactions. Incluye: direccion (Forward/Backward), modo de hidratacion
		// (Shared/Independent + opcional untilSeek), terminator del Action plane
		// (Program.Emit / Causation.Continue / Metadata.Elide / Metadata.Materialize /
		// None), patrones OnMatch literales por Seek, contadores per-Seek, y el ring
		// buffer LastMatches (hasta 32 capturas recientes con bindings).
		//
		// Match case-insensitive contra Name. Si la reaction no existe en el actor,
		// lanza LanguageException.
		//
		// Forma del Toon:
		//
		//   name: "ReplicaDeMoneda"
		//   direction: "Forward"
		//   hydration: "Shared(untilSeek: 'Propagacion')"
		//   action: "Metadata.Elide"
		//   matchCount: 17
		//   seeks:
		//     - name: "CreacionMoneda"
		//       isFinal: false
		//       onMatch:
		//         - "moneda = NuevaMoneda();"
		//       entered: 17
		//       matched: 17
		//       detected: 142
		//       confirmed: 142
		//   lastMatches:
		//     - entryId: 142
		//       occurredAt: 06/01/2026 14:22:08
		//       bindings:
		//         - name: "moneda"
		//           value: "..."
		//
		// hydration es un string compactado para mantener la salida lineal: cuando hay
		// untilSeek se inscribe entre parentesis; sin untilSeek queda solo el modo
		// ("Shared" / "Independent"). action concatena plane + verbo; Materialize
		// agrega el destination entre comillas ("Metadata.Materialize 'dest'").
		string ShowReaction(string name);

		// Dry-match de un patron DSL contra el journal actual del actor, SIN crear una
		// reaction permanente y SIN side effects observables al dominio (no journal
		// entry, no Action plane, no efectos cross-actor). Util para "ver donde pegaria"
		// una reaction antes de declararla, o para usar el motor de Reactions como una
		// consulta de correlacion sobre eventos pasados.
		//
		// Consolidacion firmada 2026-06-01: TryPattern y FindPattern producen output
		// identico y comparten implementacion — la diferencia era solo de framing
		// ("probar como pegaria" vs "encontrar eventos correlacionados"). Un solo verbo.
		//
		// El patron es la misma DSL que .Seek().OnMatch(...): bindings sin '$' son
		// nombres libres que matchean el identificador del script; bindings con '$'
		// son parametros que aparecen en los resultados ([$variable:tipo]). Las
		// clases ([_:Clase], Clase(...)) deben existir en LibraryAssemblies del actor.
		//
		// Side effect minimo: la primera invocacion con un patron dado crea una entrada
		// (formattedReaction -> reactionId) en el registro persistente de reactions del
		// DiaryStorage. Re-invocaciones del MISMO patron reusan ese id (idempotencia
		// por nombre). El nombre interno usa hash del patron — no contamina enumeracion
		// de ShowReactions porque la reaction temp NO se agrega al registry C# del
		// actor.
		//
		// Forma del Toon:
		//
		//   pattern: "<patron DSL tal cual>"
		//   matchesFound: 3
		//   matches:
		//     - entryId: 42
		//       occurredAt: 06/01/2026 14:22:08
		//       bindings:
		//         - name: "unaInstancia"
		//           value: "..."
		//         - name: "cantidad"
		//           value: 5
		//
		// Si el patron no parsea, lanza LanguageException con el error del PatternParser.
		// Si no hay matches: 'matches: []' con matchesFound: 0.
		string FindPattern(string patternDsl);

		// Devuelve el estado del pool de parametros POR FORMA (shape-keyed). Cada forma
		// es el script de una operacion V2; el pool reutiliza slots (Parameter +
		// VariableSymbol) entre invocaciones de la misma forma. highWater es el pico de
		// concurrencia historico de esa forma — la señal que apunta al tuning de la
		// logica de negocio cuando un endpoint acumula concurrencia no acotada (NO es una
		// cota de memoria; el pool crece libre y decae solo). Ordenado por highWater desc.
		//
		// Forma del Toon:
		//
		//   parameterPools:
		//     - shape: "{ box = Clase(); print box.Goo(id) v; }"
		//       live: 0          # rentadas (fuera) ahora
		//       idle: 2          # ociosas reutilizables ahora
		//       highWater: 50    # pico de concurrencia simultanea historico
		//
		// Si no hay formas vivas: 'parameterPools: []'.
		string ShowParameterPools();
	}
}
