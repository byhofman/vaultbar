namespace Vaultbar.Vault;

public sealed record KdfConfig(KdfType Type, int Iterations, int? MemoryMiB, int? Parallelism);
