using System.Runtime.CompilerServices;
using SkarpSharp.Configuration;

namespace SkarpSharp.Tests.Configuration;

/// <summary>
/// Tests never read the developer's system or personal settings file, nor SKARP_CONFIG or
/// SKARP_NO_USER_CONFIG. Tests of the levels pass their own <see cref="SettingsLocations"/>.
/// </summary>
static class NoMachineSettings
{
    [ModuleInitializer]
    internal static void Initialize() => SettingsLocations.Default = SettingsLocations.None;
}
