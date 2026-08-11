using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnitTestPuppeteer")]
[assembly: InternalsVisibleTo("Lab02BytesPerEvent")]
[assembly: InternalsVisibleTo("Lab04Tell")]
[assembly: InternalsVisibleTo("UnitTestChoreography")]
[assembly: InternalsVisibleTo("Choreography")]
[assembly: InternalsVisibleTo("UnitTestEShopOnPuppeteer")]
[assembly: InternalsVisibleTo("UnitTestGrzybekOnPuppeteer")]

// The published labs of Papers 2 and 5, which live in the puppeteer-papers repository
// and build against a clone of the public mirror beside it. They are those papers'
// measuring instruments, so a reader who follows their README has to be able to compile
// them, and three of these were granted here until a port dropped them.
//
// BenchPaper2Bdn is the one that needs saying twice. It sweeps CompiledModePolicy over
// both engines, which is the comparison Paper 2 exists to make, and since the policy's
// setter became internal a friend grant is the only route to it. That is the route
// Actor.cs names: "writable only inside the framework and its friend test assemblies,
// because choosing the engine by hand is a test affordance". Production is untouched --
// Automatic remains the default and a V2 parametric command still compiles.
[assembly: InternalsVisibleTo("BenchPaper2Bdn")]
[assembly: InternalsVisibleTo("Lab05L3InprocSymmetric")]
[assembly: InternalsVisibleTo("Lab05L4PassiveConsumer")]
[assembly: InternalsVisibleTo("Lab05L5Offline")]
