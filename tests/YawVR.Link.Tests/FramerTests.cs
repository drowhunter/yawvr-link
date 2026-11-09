using YawVR.Link.Protocol;

namespace YawVR.Link.Tests;

public class FramerTests
{
    private static TcpFramer ToDevice(YawModel model = YawModel.Yaw2) => new(TcpDirection.SenderToDevice, () => model);
    private static TcpFramer ToSender(YawModel model = YawModel.Yaw2) => new(TcpDirection.DeviceToSender, () => model);

    [Fact]
    public void Merged_reads_split_into_records()
    {
        // GameLink sends CHECK_IN then SYSTEM_INFO back to back; TCP may deliver them in one read.
        var f = ToDevice();
        f.Append(Packets.CheckIn(50011, "Game").Concat(Packets.GetSystemInfo()).Concat(Packets.SetPower(40)).ToArray());
        var records = f.ReadAll().ToList();
        Assert.Equal(new[] { CommandIds.CheckIn, CommandIds.SystemInfo, CommandIds.SetPower }, records.Select(r => r.Id));
        Assert.Equal("Game", records[0].ReadAscii(5));
        Assert.Equal(40, records[2].ReadInt32(1));
    }

    [Fact]
    public void Split_reads_wait_for_the_rest_of_a_fixed_record()
    {
        var f = ToDevice();
        var tilt = Packets.SetTiltLimits(new TiltLimits(10, 20, 30));
        f.Append(tilt.AsSpan(0, 6));
        Assert.False(f.TryRead(out _));
        f.Append(tilt.AsSpan(6));
        Assert.True(f.TryRead(out var r));
        Assert.Equal(tilt, r.Bytes);
    }

    [Fact]
    public void Stop_accepts_GameLink_park_byte_and_bare_emulator_form()
    {
        var f = ToDevice();
        f.Append(new byte[] { 0xA2, 0x01, 0xA2, 0xA1 });
        var records = f.ReadAll().ToList();
        Assert.Equal(3, records.Count);
        Assert.Equal(2, records[0].Length);
        Assert.Equal(1, records[1].Length);
        Assert.Equal(CommandIds.Start, records[2].Id);
    }

    [Fact]
    public void Vibration_length_depends_on_model()
    {
        var yaw1 = ToDevice(YawModel.Yaw1);
        yaw1.Append(new byte[] { 0x61, 0x01, 0xA1 });
        Assert.Equal(new[] { 2, 1 }, yaw1.ReadAll().Select(r => r.Length));

        var yaw2 = ToDevice(YawModel.Yaw2);
        yaw2.Append(new byte[] { 0x61, 0x01, 0x07, 0xA1 });
        Assert.Equal(new[] { 3, 1 }, yaw2.ReadAll().Select(r => r.Length));
    }

    [Fact]
    public void Parameter_dump_splits_into_echo_records_on_every_model()
    {
        foreach (var model in Enum.GetValues<YawModel>())
        {
            var f = ToSender(model);
            f.InParameterDump = true;
            f.Append(Packets.ParameterDump(DeviceParameters.DefaultsFor(model), model));
            var ids = new List<byte>();
            while (f.TryRead(out var r))
            {
                ids.Add(r.Id);
                if (r.Id == CommandIds.SetHasVibration) f.InParameterDump = false;
            }
            var expected = new List<byte> { 0x32, 0x40, 0x70, 0x62, 0x64, 0x61 };
            if (model != YawModel.Yaw3) expected.Add(CommandIds.LedConfig);
            Assert.Equal(expected, ids);
        }
    }

    [Fact]
    public void State_reply_and_button_press_in_one_read()
    {
        var f = ToSender();
        f.Append(Packets.StateAnswer(new DeviceStatus(DeviceStateKind.Parking, true)).Concat(Packets.ButtonPressed(ArcadeEvent.RedPressed)).ToArray());
        var records = f.ReadAll().ToList();
        Assert.Equal(new[] { CommandIds.GetState, CommandIds.ButtonPressed }, records.Select(r => r.Id));
        Assert.StartsWith("parking;", records[0].ReadAscii(2));
    }

    [Fact]
    public void App_check_in_answer_uses_its_length_byte()
    {
        var f = ToSender();
        var answer = Packets.CheckInAppAnswer(true, true, "Game", YawModel.Yaw1);
        f.Append(answer.Concat(Packets.Ack(CommandIds.Start)).ToArray());
        var records = f.ReadAll().ToList();
        Assert.Equal(answer, records[0].Bytes);
        Assert.Equal(CommandIds.Start, records[1].Id);
    }
}
