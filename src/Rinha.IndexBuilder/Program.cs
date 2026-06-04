using Rinha.IndexBuilder;

if (args.Length == 0) return Usage();

switch (args[0])
{
    case "selftest":
        return SelfTest.Run();

    case "build":
        if (args.Length < 3) return Usage();
        int k = IntArg(args, "--k", 2048);
        int iters = IntArg(args, "--iters", 12);
        int sample = IntArg(args, "--sample", 200_000);
        int seed = IntArg(args, "--seed", 1);
        return BuildCommand.Run(args[1], args[2], k, iters, sample, seed);

    case "diag":
    {
        if (args.Length < 4) return Usage();
        int dsample = IntArg(args, "--sample", 5000);
        int dnprobe = IntArg(args, "--nprobe", 8);
        return DiagCommand.Run(args[1], args[2], args[3], dsample, dnprobe);
    }

    default:
        return Usage();
}

static int IntArg(string[] args, string name, int def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name && int.TryParse(args[i + 1], out int v)) return v;
    return def;
}

static int Usage()
{
    Console.WriteLine("uso:");
    Console.WriteLine("  dotnet run -c Release -- selftest");
    Console.WriteLine("  dotnet run -c Release -- build <references.json.gz> <index.bin> [--k 2048] [--iters 12] [--sample 200000] [--seed 1]");
    Console.WriteLine("  dotnet run -c Release -- diag <references.json.gz> <index.bin> <test-data.json> [--sample 5000] [--nprobe 8]");
    return 0;
}
