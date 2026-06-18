using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using eBackup.Ipc.Contracts;

namespace eBackup.Ipc.Server;

/// <summary>
/// Маршрутизирует ОДИН req-фрейм в нужный метод <see cref="IIpcHandlers"/> и возвращает
/// resp- или fault-фрейм. Тело декодируется только после распознавания op (двухстадийно).
/// Неизвестная операция → Unsupported (никогда молча не игнорируем — иначе клиент висит).
/// Любое необъявленное исключение → generic Internal (детали наружу не уходят).
/// Стриминговые AttachToJob/DetachFromJob тут НЕ обслуживаются (живой цикл соединения — S4).
/// </summary>
public static class IpcDispatcher
{
    private static readonly IpcJsonContext Ctx = IpcJsonContext.Default;

    public static async Task<Frame> DispatchAsync(Frame req, IIpcHandlers h, CallerContext caller, CancellationToken ct)
    {
        if (req.Kind != FrameKinds.Req || string.IsNullOrEmpty(req.Op))
            return Fault(req.Id, IpcErrorCodes.BadRequest, "Ожидался фрейм-запрос с операцией.");

        try
        {
            return req.Op switch
            {
                IpcOps.Hello => Resp(req.Id, await h.HelloAsync(Decode(req, Ctx.HelloRequest), caller, ct), Ctx.HelloResponse),
                IpcOps.StartBackup => Resp(req.Id, await h.StartBackupAsync(Decode(req, Ctx.StartBackupRequest), caller, ct), Ctx.StartBackupResponse),
                IpcOps.RunScheduleNow => Resp(req.Id, await h.RunScheduleNowAsync(Decode(req, Ctx.RunScheduleNowRequest), caller, ct), Ctx.StartBackupResponse),
                IpcOps.CancelJob => Resp(req.Id, await h.CancelJobAsync(Decode(req, Ctx.CancelJobRequest), caller, ct), Ctx.Ack),
                IpcOps.GetJob => Resp(req.Id, await h.GetJobAsync(Decode(req, Ctx.GetJobRequest), caller, ct), Ctx.JobStatus),
                IpcOps.ListJobs => Resp(req.Id, await h.ListJobsAsync(Decode(req, Ctx.ListJobsRequest), caller, ct), Ctx.JobStatusArray),
                IpcOps.StashPassphrase => Resp(req.Id, await h.StashPassphraseAsync(Decode(req, Ctx.StashPassphraseRequest), caller, ct), Ctx.StashPassphraseResponse),

                IpcOps.ListStorages => Resp(req.Id, await h.ListStoragesAsync(caller, ct), Ctx.StorageSummaryArray),
                IpcOps.ListStorageDetails => Resp(req.Id, await h.ListStorageDetailsAsync(caller, ct), Ctx.StorageDetailArray),
                IpcOps.GetStorage => Resp(req.Id, await h.GetStorageAsync(Decode(req, Ctx.GetStorageRequest), caller, ct), Ctx.StorageDetail),
                IpcOps.UpsertStorage => Resp(req.Id, await h.UpsertStorageAsync(Decode(req, Ctx.StorageInput), caller, ct), Ctx.Ack),
                IpcOps.DeleteStorage => Resp(req.Id, await h.DeleteStorageAsync(Decode(req, Ctx.DeleteByIdRequest), caller, ct), Ctx.Ack),
                IpcOps.TestStorage => Resp(req.Id, await h.TestStorageAsync(Decode(req, Ctx.TestStorageRequest), caller, ct), Ctx.TestResult),
                IpcOps.ListArchives => Resp(req.Id, await h.ListArchivesAsync(Decode(req, Ctx.ListArchivesRequest), caller, ct), Ctx.RemoteFileDtoArray),
                IpcOps.DeleteArchive => Resp(req.Id, await h.DeleteArchiveAsync(Decode(req, Ctx.DeleteArchiveRequest), caller, ct), Ctx.Ack),

                IpcOps.ListSchedules => Resp(req.Id, await h.ListSchedulesAsync(caller, ct), Ctx.ScheduleSummaryArray),
                IpcOps.UpsertSchedule => Resp(req.Id, await h.UpsertScheduleAsync(Decode(req, Ctx.ScheduleInput), caller, ct), Ctx.Ack),
                IpcOps.DeleteSchedule => Resp(req.Id, await h.DeleteScheduleAsync(Decode(req, Ctx.DeleteByIdRequest), caller, ct), Ctx.Ack),

                IpcOps.ListModules => Resp(req.Id, await h.ListModulesAsync(caller, ct), Ctx.ModuleSummaryArray),
                IpcOps.SetModuleEnabled => Resp(req.Id, await h.SetModuleEnabledAsync(Decode(req, Ctx.SetModuleEnabledRequest), caller, ct), Ctx.Ack),
                IpcOps.InstallModule => Resp(req.Id, await h.InstallModuleAsync(Decode(req, Ctx.InstallModuleRequest), caller, ct), Ctx.Ack),

                IpcOps.ListCustomFolders => Resp(req.Id, await h.ListCustomFoldersAsync(caller, ct), Ctx.StringArray),
                IpcOps.UpsertCustomFolder => Resp(req.Id, await h.UpsertCustomFolderAsync(Decode(req, Ctx.UpsertCustomFolderRequest), caller, ct), Ctx.Ack),
                IpcOps.DeleteCustomFolder => Resp(req.Id, await h.DeleteCustomFolderAsync(Decode(req, Ctx.DeleteByIdRequest), caller, ct), Ctx.Ack),

                IpcOps.ListHistory => Resp(req.Id, await h.ListHistoryAsync(Decode(req, Ctx.ListHistoryRequest), caller, ct), Ctx.BackupRunRecordDtoArray),
                IpcOps.GetRunLog => Resp(req.Id, await h.GetRunLogAsync(Decode(req, Ctx.GetRunLogRequest), caller, ct), Ctx.GetRunLogResponse),

                _ => Fault(req.Id, IpcErrorCodes.Unsupported, $"Неизвестная или недоступная операция: {req.Op}."),
            };
        }
        catch (OperationCanceledException)
        {
            return Fault(req.Id, IpcErrorCodes.Cancelled, "Операция отменена.");
        }
        catch (IpcFaultException fe)
        {
            return Fault(req.Id, fe.Code, fe.Message, fe.Retryable);
        }
        catch (Exception)
        {
            // Детали (стек, пути) — только в лог службы, не клиенту через границу привилегий.
            return Fault(req.Id, IpcErrorCodes.Internal, "Внутренняя ошибка службы.");
        }
    }

    private static T Decode<T>(Frame req, JsonTypeInfo<T> ti)
    {
        if (req.Body is not { } body)
            throw new IpcFaultException(IpcErrorCodes.BadRequest, "Отсутствует тело запроса.");
        return body.Deserialize(ti)
            ?? throw new IpcFaultException(IpcErrorCodes.BadRequest, "Пустое тело запроса.");
    }

    internal static Frame Resp<T>(string? id, T value, JsonTypeInfo<T> ti)
        => new() { Kind = FrameKinds.Resp, Id = id, Body = JsonSerializer.SerializeToElement(value, ti) };

    internal static Frame Fault(string? id, string code, string message, bool retryable = false)
        => new()
        {
            Kind = FrameKinds.Fault,
            Id = id,
            Body = JsonSerializer.SerializeToElement(
                new IpcError { Code = code, Message = message, Retryable = retryable }, Ctx.IpcError),
        };
}
