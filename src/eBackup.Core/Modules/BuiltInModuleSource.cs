using eBackup.Core.Abstractions;

namespace eBackup.Core.Modules;

/// <summary>Источник встроенных (компилируемых в приложение) код-модулей, напр. OBS.</summary>
public sealed class BuiltInModuleSource(IEnumerable<IBackupModule> modules) : IModuleSource
{
    public ModuleSource Kind => ModuleSource.BuiltIn;

    public IEnumerable<ModuleDescriptor> Discover()
        => modules.Select(m => new ModuleDescriptor
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Source = ModuleSource.BuiltIn,
            Instance = m,
            Trust = ModuleTrust.Trusted,
            Origin = "built-in"
        });
}
