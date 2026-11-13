using BenchmarkDotNet.Running;

// The benchmarks are selected with the arguments of BenchmarkDotNet, e.g. --filter * or --filter *StaticDispatch*.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
