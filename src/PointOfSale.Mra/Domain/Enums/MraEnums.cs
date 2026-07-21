namespace PointOfSale.Mra.Domain.Enums;

public enum TerminalActivationStatus : byte
{
    NotActivated = 0,
    PendingConfirmation = 1,
    Activated = 2,
    Deactivated = 3
}

public enum ConfigurationScope : byte
{
    Global = 1,
    Terminal = 2,
    Taxpayer = 3
}

public enum ConfigurationSource
{
    Activation,
    GetLatestConfigs,
    Manual
}

public enum OfflineQueueStatus : byte
{
    Pending = 0,
    InProgress = 1,
    Submitted = 2,
    Failed = 3,
    Quarantined = 4
}

public enum MraApiEnvironment
{
    Dev,
    Production
}
