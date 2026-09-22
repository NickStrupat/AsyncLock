using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BenchmarkSuite;
using NickStrupat;

var config = DefaultConfig.Instance;

Type Selector(Type x) => typeof(Benchmarks<>).MakeGenericType(x);

Type[] asyncLockTypes = Enumerable.Select([typeof(AsyncLock), typeof(AsyncLock7), typeof(AsyncSemaphoreSlimLock)], Selector).ToArray();
var summary = BenchmarkRunner.Run(asyncLockTypes, config, args);

// Use this to select benchmarks from the console:
// var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);