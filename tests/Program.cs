using Quire.Cli.Configuration;

string vault = Path.Combine(Path.GetTempPath(), "quire-tests-vault-" + Guid.NewGuid().ToString("N"));
string? previous = SecretKeyStore.StorageDirectoryOverride;
SecretKeyStore.StorageDirectoryOverride = vault;
try { return await Suite.RunAsync(args); }
finally
{
    SecretKeyStore.StorageDirectoryOverride = previous;
    if (Directory.Exists(vault)) Directory.Delete(vault, true);
}
