using System.Text.Json;
using eBackup.Ipc.Contracts;
using Xunit;

namespace eBackup.Tests.Ipc;

public class IpcContractTests
{
    private static readonly IpcJsonContext Ctx = IpcJsonContext.Default;

    [Fact]
    public void Frame_With_Body_RoundTrips_TwoStage()
    {
        var req = new StartBackupRequest
        {
            ModuleIds = ["obs", "ssh"],
            TargetStorageIds = ["nas"],
            CompressionMode = 1,
            Trigger = "вручную",
            ClientRequestId = "req-1",
        };
        var frame = new Frame
        {
            Kind = FrameKinds.Req,
            Id = "1",
            Op = IpcOps.StartBackup,
            Body = JsonSerializer.SerializeToElement(req, Ctx.StartBackupRequest),
        };

        var json = JsonSerializer.Serialize(frame, Ctx.Frame);
        var back = JsonSerializer.Deserialize(json, Ctx.Frame)!;

        Assert.Equal(FrameKinds.Req, back.Kind);
        Assert.Equal(IpcOps.StartBackup, back.Op);
        Assert.Equal("1", back.Id);

        // Тело декодируем только теперь, когда op распознан (двухстадийный декод).
        var decoded = back.Body!.Value.Deserialize(Ctx.StartBackupRequest)!;
        Assert.Equal(["obs", "ssh"], decoded.ModuleIds);
        Assert.Equal("вручную", decoded.Trigger);
        Assert.Equal("req-1", decoded.ClientRequestId);
    }

    [Fact]
    public void Serialization_Is_CamelCase_And_Omits_Null()
    {
        var frame = new Frame { Kind = FrameKinds.Note, Op = IpcNotes.Phase }; // Id/Seq = null
        var json = JsonSerializer.Serialize(frame, Ctx.Frame);

        Assert.Contains("\"kind\"", json);
        Assert.DoesNotContain("\"Kind\"", json);
        Assert.DoesNotContain("\"id\"", json);  // null опущен
        Assert.DoesNotContain("\"seq\"", json); // null опущен
    }

    [Fact]
    public void StartBackupRequest_Uses_CamelCase_Members()
    {
        var json = JsonSerializer.Serialize(new StartBackupRequest { ModuleIds = ["x"] }, Ctx.StartBackupRequest);
        Assert.Contains("\"moduleIds\"", json);
    }

    [Fact]
    public void Unknown_Property_Is_Ignored()
    {
        // Сервер новее → лишнее поле; старый декод должен его проигнорировать, не упасть.
        const string json = """{"moduleIds":["a"],"trigger":"t","futureField":123,"nested":{"x":1}}""";
        var req = JsonSerializer.Deserialize(json, Ctx.StartBackupRequest)!;
        Assert.Equal(["a"], req.ModuleIds);
        Assert.Equal("t", req.Trigger);
    }

    [Fact]
    public void Unknown_Frame_Kind_And_Op_Do_Not_Throw()
    {
        const string json = """{"v":1,"kind":"futureKind","op":"futureOp","body":{"anything":true}}""";
        var frame = JsonSerializer.Deserialize(json, Ctx.Frame)!;
        Assert.Equal("futureKind", frame.Kind); // строкой — никакого исключения на неизвестном виде
        Assert.Equal("futureOp", frame.Op);
        // Тело НЕ декодируем — op не распознан. Просто доступно как JsonElement.
        Assert.Equal(JsonValueKind.Object, frame.Body!.Value.ValueKind);
    }

    [Fact]
    public void Enum_Like_String_Field_Accepts_Future_Value()
    {
        // Поля-«enum» едут строкой → будущее значение не роняет декод (маппинг в Unknown — забота кода).
        const string json = """{"id":"s1","name":"n","kind":"FutureKind"}""";
        var sched = JsonSerializer.Deserialize(json, Ctx.ScheduleInput)!;
        Assert.Equal("FutureKind", sched.Kind);
    }
}
