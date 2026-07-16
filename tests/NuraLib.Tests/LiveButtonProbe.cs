using NuraLib.Configuration;
using NuraLib.Crypto;
using NuraLib.Devices;
using NuraLib.Logging;
using NuraLib.Protocol;
using NuraLib.Transport;
using NuraLib.Utilities;

namespace NuraLib.Tests;

// Temporary live diagnostics for a connected device. Modes (passed as args after "probe"):
//   buttons            - dump button configuration across profile ids / command variants
//   profiles           - read current hearing profile id + profile names + visualisation presence
//   select <id>        - select hearing profile <id> and read the current id back (tests audio restore)
internal static class LiveButtonProbe {
    public static async Task RunAsync(string[] args) {
        var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "buttons";

        var path = Path.Combine(Environment.CurrentDirectory, "nura-config.json");
        var config = NuraConfigStore.LoadOrCreate(path);
        if (config.Devices.Count == 0) {
            Console.WriteLine("No devices in nura-config.json.");
            return;
        }

        var device = config.Devices[0];
        Console.WriteLine($"Device: {device.FriendlyName} serial={device.DeviceSerial} fw={device.FirmwareVersion} addr={device.DeviceAddress}");

        var logger = new NuraClientLogger(e => {
            if ((int)e.Level >= (int)NuraLogLevel.Information) {
                Console.WriteLine($"[{e.Level}] {e.Source}: {e.Message}");
            }
        });

        var transport = new RfcommHeadsetTransport(logger);
        await transport.ConnectAsync(device.DeviceAddress, CancellationToken.None);

        var runtime = NuraSessionRuntime.Create(device);
        await NuraLocalSessionSupport.PerformAppHandshakeAsync(runtime, transport, logger, CancellationToken.None);

        switch (mode) {
            case "profiles":
                await ProbeProfilesAsync(transport, runtime);
                break;
            case "select":
                var id = args.Length > 2 ? int.Parse(args[2]) : 0;
                await SelectProfileAsync(transport, runtime, id);
                break;
            case "perso":
                // perso 0 = Neutral (personalisation OFF / flat audio), perso 1 = Personalised
                var personalised = args.Length > 2 && args[2] == "1";
                await SetPersonalisationAsync(transport, runtime, personalised);
                break;
            default:
                await ProbeButtonsAsync(transport, runtime);
                break;
        }

        Console.WriteLine("==== PROBE COMPLETE ====");
    }

    private static async Task ProbeProfilesAsync(RfcommHeadsetTransport transport, NuraSessionRuntime runtime) {
        Console.WriteLine("==== HEARING PROFILE PROBE ====");
        await ReadCurrentProfileAsync(transport, runtime);

        for (var profile = 0; profile < 3; profile++) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try {
                var name = await transport.ExecuteAsync(NuraCommandFactory.CreateGetProfileName(profile), runtime, cts.Token);
                Console.WriteLine($"profile {profile} name = '{name ?? "<null>"}'");
            } catch (Exception ex) {
                Console.WriteLine($"profile {profile} name -> ERROR {ex.GetType().Name}: {ex.Message}");
            }

            using var vcts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try {
                var vis = await transport.ExecuteAsync(NuraCommandFactory.CreateGetVisualisationData(profile), runtime, vcts.Token);
                Console.WriteLine($"profile {profile} visualisation = {(vis is null ? "<null>" : "present")}");
            } catch (Exception ex) {
                Console.WriteLine($"profile {profile} visualisation -> ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static async Task SelectProfileAsync(RfcommHeadsetTransport transport, NuraSessionRuntime runtime, int profileId) {
        Console.WriteLine($"==== SELECT HEARING PROFILE {profileId} ====");
        await ReadCurrentProfileAsync(transport, runtime);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6))) {
            try {
                var ack = await transport.ExecuteAsync(NuraCommandFactory.CreateSelectProfile(profileId), runtime, cts.Token);
                Console.WriteLine($"SelectProfile({profileId}) ack = {HexEncoding.Format(ack)}");
            } catch (Exception ex) {
                Console.WriteLine($"SelectProfile({profileId}) -> ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        await ReadCurrentProfileAsync(transport, runtime);
        Console.WriteLine(">>> Listen now: is there sound?");
    }

    private static async Task SetPersonalisationAsync(RfcommHeadsetTransport transport, NuraSessionRuntime runtime, bool personalised) {
        Console.WriteLine($"==== SET PERSONALISATION mode={(personalised ? "Personalised" : "Neutral")} ====");

        using (var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(6))) {
            try {
                var current = await transport.ExecuteAsync(NuraCommandFactory.CreateGetKickitEnabled(), runtime, rcts.Token);
                Console.WriteLine($"current personalisation mode = {current}");
            } catch (Exception ex) {
                Console.WriteLine($"read personalisation mode -> ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        using (var scts = new CancellationTokenSource(TimeSpan.FromSeconds(6))) {
            try {
                var ack = await transport.ExecuteAsync(NuraCommandFactory.CreateSetKickitEnabled(personalised), runtime, scts.Token);
                Console.WriteLine($"SetKickitEnabled({personalised}) ack = {HexEncoding.Format(ack)}");
            } catch (Exception ex) {
                Console.WriteLine($"set personalisation -> ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        using (var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(6))) {
            try {
                var after = await transport.ExecuteAsync(NuraCommandFactory.CreateGetKickitEnabled(), runtime, rcts.Token);
                Console.WriteLine($"personalisation mode after = {after}");
            } catch (Exception ex) {
                Console.WriteLine($"read-back personalisation mode -> ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(">>> Play audio now and listen: is there sound?");
    }

    private static async Task ReadCurrentProfileAsync(RfcommHeadsetTransport transport, NuraSessionRuntime runtime) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try {
            var current = await transport.ExecuteAsync(NuraCommandFactory.CreateGetCurrentProfileId(), runtime, cts.Token);
            Console.WriteLine($"current profile id = {current} (0x{current:X2})");
        } catch (Exception ex) {
            Console.WriteLine($"current profile -> ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task ProbeButtonsAsync(RfcommHeadsetTransport transport, NuraSessionRuntime runtime) {
        byte[] commandLowBytes = [0x51, 0xB7, 0x73];
        int[] profiles = [0, 1, 2, 0xFF];

        Console.WriteLine("==== BUTTON CONFIG PROBE ====");
        foreach (var cmdLow in commandLowBytes) {
            foreach (var profile in profiles) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                try {
                    var raw = await transport.ExecuteAsync(new RawAppEncryptedProbe(cmdLow, profile), runtime, cts.Token);
                    Console.WriteLine($"cmd=0x{cmdLow:x2} profile={profile,3} -> len={raw.Length} hex={HexEncoding.Format(raw)}");
                } catch (Exception ex) {
                    Console.WriteLine($"cmd=0x{cmdLow:x2} profile={profile,3} -> ERROR {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private sealed class RawAppEncryptedProbe : NuraAppEncryptedCommand<byte[]> {
        private readonly byte _commandLow;
        private readonly int _profileId;

        public RawAppEncryptedProbe(byte commandLow, int profileId) {
            _commandLow = commandLow;
            _profileId = profileId;
        }

        public override string Name => $"RawProbe(cmd=0x{_commandLow:x2},profile={_profileId})";

        protected override byte[] CreatePlainPayload() => [0x00, _commandLow, checked((byte)_profileId)];

        protected override byte[] ParsePlainPayload(byte[] plainPayload) => plainPayload;
    }
}
