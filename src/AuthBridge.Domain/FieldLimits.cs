namespace AuthBridge.Domain;

/// <summary>Column and input bounds shared by validation and both provider mappings.</summary>
public static class FieldLimits
{
    public const int PublicId = 40;
    public const int TenantId = 40;
    public const int PayerCode = 40;
    public const int ServiceCode = 40;
    public const int RuleVersion = 20;
    public const int DocumentType = 60;
    public const int FixtureKey = 80;
    public const int IdempotencyKey = 128;
    public const int ActorId = 128;
    public const int EnumValue = 30;
    public const int PayloadHash = 64;
    public const int Summary = 500;
    public const int Reason = 300;
    public const int DisplayLabel = 100;
    public const int Operation = 60;
    public const int Target = 100;
    public const int Outcome = 60;
    public const int CorrelationId = 64;
    public const int PayerReference = 40;
    public const int LastError = 200;

    public const int MaxPageSize = 100;
}
