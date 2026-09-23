using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BenchmarkSuite;
using BenchmarkSuite.ThirdParty;
using NickStrupat;

var config = DefaultConfig.Instance;

Type[] asyncLockTypes =
[
	typeof(AsyncLock1),
	typeof(AsyncLock),
	typeof(AsyncSemaphoreSlimLock),
	typeof(NitoAsyncLock),
	typeof(DotNextAsyncExclusiveLock),
	typeof(VsThreadingAsyncSemaphore),
];
Type[] benchmarkTypes = [typeof(Benchmarks<>), typeof(CancellationBenchmarks<>)];

var benchmarks = benchmarkTypes
	.SelectMany(benchmark => asyncLockTypes.Select(asyncLock => benchmark.MakeGenericType(asyncLock)))
	.ToArray();
var summary = BenchmarkRunner.Run(benchmarks, config, args);

// Use this to select benchmarks from the console:
// var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);