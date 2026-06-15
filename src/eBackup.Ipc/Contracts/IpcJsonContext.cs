using System.Text.Json.Serialization;

namespace eBackup.Ipc.Contracts;

/// <summary>
/// Source-generated сериализация контракта. CamelCase, без отступов, null опускаем.
/// БЕЗ JsonStringEnumConverter — enum'ы на проводе едут СТРОКАМИ в string-полях и
/// маппятся в коде в Unknown, иначе неизвестное значение бросало бы исключение.
/// (Сознательно НЕ ManifestJson.Options — та рефлексивная и с отступами.)
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Frame))]
[JsonSerializable(typeof(IpcError))]
[JsonSerializable(typeof(PassphraseRef))]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(HelloResponse))]
[JsonSerializable(typeof(StartBackupRequest))]
[JsonSerializable(typeof(StartBackupResponse))]
[JsonSerializable(typeof(RunScheduleNowRequest))]
[JsonSerializable(typeof(CancelJobRequest))]
[JsonSerializable(typeof(GetJobRequest))]
[JsonSerializable(typeof(ListJobsRequest))]
[JsonSerializable(typeof(AttachRequest))]
[JsonSerializable(typeof(DetachRequest))]
[JsonSerializable(typeof(StashPassphraseRequest))]
[JsonSerializable(typeof(StashPassphraseResponse))]
[JsonSerializable(typeof(DeleteByIdRequest))]
[JsonSerializable(typeof(StorageInput))]
[JsonSerializable(typeof(TestStorageRequest))]
[JsonSerializable(typeof(ScheduleInput))]
[JsonSerializable(typeof(SetModuleEnabledRequest))]
[JsonSerializable(typeof(InstallModuleRequest))]
[JsonSerializable(typeof(ListHistoryRequest))]
[JsonSerializable(typeof(GetRunLogRequest))]
[JsonSerializable(typeof(GetRunLogResponse))]
[JsonSerializable(typeof(JobStatus))]
[JsonSerializable(typeof(JobStatus[]))]
[JsonSerializable(typeof(AttachAck))]
[JsonSerializable(typeof(StorageSummary))]
[JsonSerializable(typeof(StorageSummary[]))]
[JsonSerializable(typeof(ScheduleSummary))]
[JsonSerializable(typeof(ScheduleSummary[]))]
[JsonSerializable(typeof(ModuleSummary))]
[JsonSerializable(typeof(ModuleSummary[]))]
[JsonSerializable(typeof(BackupRunRecordDto))]
[JsonSerializable(typeof(BackupRunRecordDto[]))]
[JsonSerializable(typeof(TestResult))]
[JsonSerializable(typeof(LogLine))]
[JsonSerializable(typeof(Ack))]
[JsonSerializable(typeof(PhaseNote))]
[JsonSerializable(typeof(LogNote))]
[JsonSerializable(typeof(StateChangedNote))]
[JsonSerializable(typeof(LogTruncatedNote))]
[JsonSerializable(typeof(ResyncNote))]
[JsonSerializable(typeof(CompletedNote))]
public partial class IpcJsonContext : JsonSerializerContext
{
}
