using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;

var keyStorePath = Path.Combine(Path.GetTempPath(), "keyringguard-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(keyStorePath);

try
{
    var factory = new KeyRingProviderFactory(
        _ => DataProtectionProvider.Create(
            new DirectoryInfo(keyStorePath),
            builder => builder.SetApplicationName("Sample.App")),
        TimeSpan.FromSeconds(5));

    var result = await KeyRingVerifier.VerifyAsync(
        KeyRingScenario.RestartContinuity,
        factory);

    if (!result.Succeeded)
    {
        throw new InvalidOperationException(result.Message);
    }

    Console.WriteLine("KeyRingGuard quick start: PASS");
}
finally
{
    Directory.Delete(keyStorePath, recursive: true);
}
