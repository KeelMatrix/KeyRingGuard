using KeelMatrix.KeyRingGuard;
using Microsoft.AspNetCore.DataProtection;

var storePath = Path.Combine(Path.GetTempPath(), "keyringguard-consumer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(storePath);

try
{
    KeyRingProviderFactory CreateFactory(string applicationName) => new(
        _ =>
        {
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(storePath),
                builder => builder.SetApplicationName(applicationName));
            return provider;
        },
        TimeSpan.FromSeconds(5));

    var continuity = await KeyRingVerifier.VerifyAsync(
        KeyRingScenario.RestartContinuity,
        CreateFactory("Consumer.App"));
    if (!continuity.Succeeded)
    {
        throw new InvalidOperationException(continuity.Message);
    }

    var isolation = await KeyRingVerifier.VerifyAsync(
        KeyRingScenario.ApplicationIsolation,
        CreateFactory("Consumer.App"),
        CreateFactory("Other.App"));
    if (!isolation.Succeeded)
    {
        throw new InvalidOperationException(isolation.Message);
    }

    Console.WriteLine("PACKAGE_CONSUMER_SMOKE: PASS");
}
finally
{
    if (Directory.Exists(storePath))
    {
        Directory.Delete(storePath, recursive: true);
    }
}
