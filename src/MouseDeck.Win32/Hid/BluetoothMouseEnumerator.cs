using MouseDeck.Core.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MouseDeck.Win32.Hid;

/// <summary>
/// Discovers ANY connected Bluetooth LE or Classic Bluetooth mouse via native Windows WinRT APIs
/// and reads battery level from Windows PnP DEVPKEY_Bluetooth_Battery ({104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2)
/// with optional GATT Battery Service (0x180F / 0x2A19) fallback.
/// Zero driver modification, zero vendor-software conflict, 0% idle CPU.
/// </summary>
public static class BluetoothMouseEnumerator
{
    private const string DevPkeyBluetoothBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PropIsConnected = "System.Devices.Aep.IsConnected";
    private const string PropCategory = "System.Devices.Aep.Category";
    private const string PropContainerId = "System.Devices.ContainerId";
    private const string PropDeviceAddress = "System.Devices.Aep.DeviceAddress";

    // AQS filter for paired Bluetooth Classic & BLE Association Endpoints
    private const string BluetoothAepSelector =
        "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
        "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\") AND " +
        "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

    private static readonly Guid GattBatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid GattBatteryLevelCharUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    public static async Task<IReadOnlyList<MouseDeviceInfo>> EnumerateAsync(CancellationToken ct = default)
    {
        var results = new List<MouseDeviceInfo>();
        try
        {
            string[] requestedProps =
            [
                PropIsConnected,
                PropCategory,
                PropContainerId,
                PropDeviceAddress,
                DevPkeyBluetoothBattery
            ];

            var devices = await DeviceInformation.FindAllAsync(BluetoothAepSelector, requestedProps).AsTask(ct);
            if (devices is null || devices.Count == 0)
                return results;

            // Also scan PnP battery nodes keyed by ContainerId (Windows stores BLE/BT battery on sibling PnP nodes)
            var containerBatteryMap = await QueryPnPBatteryByContainerAsync(ct);

            foreach (var dev in devices)
            {
                if (!LooksLikeBluetoothMouse(dev))
                    continue;

                bool connected = dev.Properties.TryGetValue(PropIsConnected, out var connObj) && connObj is true;
                int? battery = ExtractByteBattery(dev.Properties, DevPkeyBluetoothBattery);

                if (!battery.HasValue && dev.Properties.TryGetValue(PropContainerId, out var cidObj) && cidObj is Guid cid)
                {
                    if (containerBatteryMap.TryGetValue(cid, out int pnpBat))
                        battery = pnpBat;
                }

                string addr = dev.Properties.TryGetValue(PropDeviceAddress, out var addrObj) && addrObj is string s
                    ? s.Replace(":", string.Empty)
                    : dev.Id;

                results.Add(new MouseDeviceInfo
                {
                    DevicePath = $"bt://{dev.Id}",
                    ProductName = string.IsNullOrWhiteSpace(dev.Name) ? "Bluetooth Mouse" : dev.Name.Trim(),
                    ManufacturerName = "Bluetooth",
                    SerialNumber = addr,
                    IsBluetooth = true,
                    IsBluetoothConnected = connected,
                    OsBatteryPercent = battery,
                    CanOpenReadWrite = true
                });
            }
        }
        catch
        {
            // Bluetooth stack unavailable or disabled
        }

        return results;
    }

    /// <summary>
    /// Attempts direct WinRT BLE GATT Battery Service (0x180F / 0x2A19) read using cached mode first
    /// (zero extra RF wakeups when Windows already cached the level).
    /// </summary>
    public static async Task<int?> TryReadGattBatteryAsync(string devicePath, CancellationToken ct = default)
    {
        if (!devicePath.StartsWith("bt://", StringComparison.OrdinalIgnoreCase))
            return null;

        string aepId = devicePath[5..];
        try
        {
            using var ble = await BluetoothLEDevice.FromIdAsync(aepId).AsTask(ct);
            if (ble is null || ble.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return null;

            var svcResult = await ble.GetGattServicesForUuidAsync(GattBatteryServiceUuid, BluetoothCacheMode.Cached).AsTask(ct);
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
                return null;

            using var svc = svcResult.Services[0];
            var charResult = await svc.GetCharacteristicsForUuidAsync(GattBatteryLevelCharUuid, BluetoothCacheMode.Cached).AsTask(ct);
            if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
                return null;

            var readResult = await charResult.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (readResult.Status != GattCommunicationStatus.Success || readResult.Value is null || readResult.Value.Length == 0)
                return null;

            using var reader = DataReader.FromBuffer(readResult.Value);
            byte pct = reader.ReadByte();
            return pct is >= 1 and <= 100 ? pct : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Dictionary<Guid, int>> QueryPnPBatteryByContainerAsync(CancellationToken ct)
    {
        var map = new Dictionary<Guid, int>();
        try
        {
            string[] props = [PropContainerId, DevPkeyBluetoothBattery];
            var pnpDevices = await DeviceInformation.FindAllAsync(
                string.Empty,
                props,
                DeviceInformationKind.Device).AsTask(ct);

            foreach (var d in pnpDevices)
            {
                int? bat = ExtractByteBattery(d.Properties, DevPkeyBluetoothBattery);
                if (bat.HasValue && d.Properties.TryGetValue(PropContainerId, out var cidObj) && cidObj is Guid cid && cid != Guid.Empty)
                {
                    map[cid] = bat.Value;
                }
            }
        }
        catch { }

        return map;
    }

    private static int? ExtractByteBattery(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var val) || val is null)
            return null;

        int raw = val switch
        {
            byte b => b,
            int i => i,
            uint u => (int)u,
            short s => s,
            ushort us => us,
            _ => -1
        };

        return raw is >= 1 and <= 100 ? raw : null;
    }

    private static bool LooksLikeBluetoothMouse(DeviceInformation dev)
    {
        if (dev.Properties.TryGetValue(PropCategory, out var catObj) && catObj is IEnumerable<string> cats)
        {
            foreach (var c in cats)
            {
                if (c.Contains("Mouse", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (c.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ||
                    c.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                    c.Contains("Headphones", StringComparison.OrdinalIgnoreCase) ||
                    c.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ||
                    c.Contains("Phone", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        var name = dev.Name ?? string.Empty;
        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx master", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx anywhere", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx vertical", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("orochi", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("basilisk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("deathadder", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("viper", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("aerox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("superlight", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("g305", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("g502", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("vxe", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mchose", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("zaopin", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("darmoshark", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("attack shark", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("keychron m", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rapoo", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("magic mouse", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
