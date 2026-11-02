using BenchmarkDotNet.Running;
using Ef.Mems.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(PredicateBenchmarks).Assembly).Run(args);
