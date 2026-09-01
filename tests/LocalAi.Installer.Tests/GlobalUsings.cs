global using Xunit;

// The installer speaks one language at a time, and which one is process state: there is one
// window, and it cannot be in two languages at once. A class that sets the language therefore
// cannot run beside a class that reads it, and xunit parallelises across classes by default —
// which is how a test asserting English first read a choice another class had just made.
//
// Every class in this assembly reads text the language decides, so the boundary is drawn around
// the assembly rather than around a collection somebody has to remember to join: one collection
// for the whole assembly, and xunit never runs two tests of one collection at the same time.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
