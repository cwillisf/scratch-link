using Fleck;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace scratch_link
{
    internal class BTSession : Session
    {
        // Things we can look for are listed here:
        // <a href="https://docs.microsoft.com/en-us/windows/uwp/devices-sensors/device-information-properties" />

        /// <summary>
        /// Signal strength property
        /// </summary>
        private const string SignalStrengthPropertyName = "System.Devices.Aep.SignalStrength";

        /// <summary>
        /// Indicates that the device returned is actually available and not discovered from a cache
        /// NOTE: This property is not currently used since it reports 'False' for paired devices
        /// which are currently advertising and within discoverable range.
        /// </summary>
        private const string IsPresentPropertyName = "System.Devices.Aep.IsPresent";

        /// <summary>
        /// Bluetooth MAC address
        /// </summary>
        private const string BluetoothAddressPropertyName = "System.Devices.Aep.DeviceAddress";

        /// <summary>
        /// PIN code for pairing
        /// </summary>
        private string _pairingCode = null;

        /// <summary>
        /// PIN code for auto-pairing
        /// </summary>
        private string _autoPairingCode = "0000";

        /// <summary>
        /// If non-null, discovery should only return a peripheral if its name is exactly this.
        /// </summary>
        private string _filterName = null;

        /// <summary>
        /// If non-null, discovery should only return a peripheral if its name starts with this.
        /// </summary>
        private string _filterNamePrefix = null;

        /// <summary>
        /// Peripheral IDs for discovered peripherals, whether or not they pass discovery filters.
        /// Double-check filters before allowing a client to connect to any peripheral in this list!
        /// </summary>
        private readonly HashSet<DeviceInformation> _discoveredPeripherals = new HashSet<DeviceInformation>();

        private DeviceWatcher _watcher;
        private StreamSocket _connectedSocket;
        private DataWriter _socketWriter;
        private DataReader _socketReader;

        internal BTSession(IWebSocketConnection webSocket) : base(webSocket)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_watcher != null &&
                (_watcher.Status == DeviceWatcherStatus.Started ||
                 _watcher.Status == DeviceWatcherStatus.EnumerationCompleted))
            {
                _watcher.Stop();
            }
            if (_connectedSocket != null)
            {
                _socketReader.Dispose();
                _socketWriter.Dispose();
                _connectedSocket.Dispose();
            }
        }

        protected override async Task DidReceiveCall(string method, JObject parameters,
            Func<JToken, JsonRpcException, Task> completion)
        {
            switch (method)
            {
                case "discover":
                    Discover(parameters);
                    await completion(null, null);
                    break;
                case "connect":
                    if (_watcher != null && _watcher.Status == DeviceWatcherStatus.Started)
                    {
                        _watcher.Stop();
                    }
                    await Connect(parameters);
                    await completion(null, null);
                    break;
                case "send":
                    await completion(await SendMessage(parameters), null);
                    break;
                default:
                    // unrecognized method: pass to base class
                    await base.DidReceiveCall(method, parameters, completion);
                    break;
            }
        }

        private void Discover(JObject parameters)
        {
            var major = parameters["majorDeviceClass"]?.ToObject<BluetoothMajorClass>();
            var minor = parameters["minorDeviceClass"]?.ToObject<BluetoothMinorClass>();
            if (major == null || minor == null)
            {
                throw JsonRpcException.InvalidParams("majorDeviceClass and minorDeviceClass required");
            }

            var filterName = parameters["name"]?.ToString();
            var filterNamePrefix = parameters["namePrefix"]?.ToString();

            var deviceClass = BluetoothClassOfDevice.FromParts(major.Value, minor.Value,
                    BluetoothServiceCapabilities.None);
            var selector = BluetoothDevice.GetDeviceSelectorFromClassOfDevice(deviceClass);

            if (_watcher != null)
            {
                _watcher.Stop();
            }

            try
            {
                _watcher = DeviceInformation.CreateWatcher(selector, new List<String>
                {
                    SignalStrengthPropertyName,
                    IsPresentPropertyName,
                    BluetoothAddressPropertyName
                });
                _watcher.Added += PeripheralDiscovered;
                _watcher.EnumerationCompleted += EnumerationCompleted;
                _watcher.Updated += PeripheralUpdated;
                _watcher.Stopped += EnumerationStopped;
                _filterName = filterName;
                _filterNamePrefix = filterNamePrefix;
                _discoveredPeripherals.Clear();
                _watcher.Start();
            }
            catch (ArgumentException)
            {
                throw JsonRpcException.ApplicationError("Failed to create device watcher");
            }
        }

        private async Task Connect(JObject parameters)
        {
            if (_connectedSocket?.Information.RemoteHostName != null)
            {
                throw JsonRpcException.InvalidRequest("Already connected");
            }
            var id = parameters["peripheralId"]?.ToObject<string>();

            if (_discoveredPeripherals.Where(peripheral => peripheral.Id == id).Any())
            {
                throw JsonRpcException.InvalidParams("Cannot connect to non-discovered peripheral");
            }

            var address = Convert.ToUInt64(id, 16);
            var bluetoothDevice = await BluetoothDevice.FromBluetoothAddressAsync(address);

            // Don't expose non/existence of a particular peripheral ID when that device doesn't pass filters
            if (bluetoothDevice == null || !DevicePassesFilters(bluetoothDevice.DeviceInformation))
            {
                throw JsonRpcException.InvalidParams("Cannot connect to requested peripheral");
            }

            if (!bluetoothDevice.DeviceInformation.Pairing.IsPaired)
            {
                if (parameters.TryGetValue("pin", out var pin))
                {
                    _pairingCode = (string)pin;
                }
                var pairingResult = await Pair(bluetoothDevice);
                if (pairingResult != DevicePairingResultStatus.Paired &&
                    pairingResult != DevicePairingResultStatus.AlreadyPaired)
                {
                    throw JsonRpcException.ApplicationError("Could not automatically pair with peripheral");
                }
            }

            var services = await bluetoothDevice.GetRfcommServicesForIdAsync(RfcommServiceId.SerialPort,
                BluetoothCacheMode.Uncached);
            if (services.Services.Count > 0)
            {
                _connectedSocket = new StreamSocket();
                await _connectedSocket.ConnectAsync(services.Services[0].ConnectionHostName,
                    services.Services[0].ConnectionServiceName);
                _socketWriter = new DataWriter(_connectedSocket.OutputStream);
                _socketReader = new DataReader(_connectedSocket.InputStream) { ByteOrder = ByteOrder.LittleEndian };
                ListenForMessages();
            }
            else
            {
                throw JsonRpcException.ApplicationError("Cannot read services from peripheral");
            }
        }

        private async Task<DevicePairingResultStatus> Pair(BluetoothDevice bluetoothDevice)
        {
            bluetoothDevice.DeviceInformation.Pairing.Custom.PairingRequested += CustomOnPairingRequested;
            var pairingResult = (DevicePairingResult)null;
            if (_pairingCode == null)
            {
                _pairingCode = _autoPairingCode;
                pairingResult = await bluetoothDevice.DeviceInformation.Pairing.Custom.PairAsync(
                    DevicePairingKinds.ConfirmOnly);
            }
            else
            {
                pairingResult = await bluetoothDevice.DeviceInformation.Pairing.Custom.PairAsync(
                    DevicePairingKinds.ProvidePin);
            }
            bluetoothDevice.DeviceInformation.Pairing.Custom.PairingRequested -= CustomOnPairingRequested;
            return pairingResult.Status;
        }

        private async Task<JToken> SendMessage(JObject parameters)
        {
            if (_socketWriter == null)
            {
                throw JsonRpcException.InvalidRequest("Not connected to peripheral");
            }

            var data = EncodingHelpers.DecodeBuffer(parameters);
            try
            {
                _socketWriter.WriteBytes(data);
                await _socketWriter.StoreAsync();
            }
            catch (ObjectDisposedException)
            {
                throw JsonRpcException.InvalidRequest("Not connected to peripheral");
            }
            return data.Length;
        }

        private async void ListenForMessages()
        {
            try
            {
                while (true)
                {
                    await _socketReader.LoadAsync(sizeof(UInt16));
                    var messageSize = _socketReader.ReadUInt16();
                    var headerBytes = BitConverter.GetBytes(messageSize);

                    var messageBytes = new byte[messageSize];
                    await _socketReader.LoadAsync(messageSize);
                    _socketReader.ReadBytes(messageBytes);

                    var totalBytes = new byte[headerBytes.Length + messageSize];
                    Array.Copy(headerBytes, totalBytes, headerBytes.Length);
                    Array.Copy(messageBytes, 0, totalBytes, headerBytes.Length, messageSize);

                    var parameters = EncodingHelpers.EncodeBuffer(totalBytes, "base64");
                    SendRemoteRequest("didReceiveMessage", parameters);
                }
            }
            catch (Exception e)
            {
                await SendErrorNotification(JsonRpcException.ApplicationError("Peripheral connection closed"));
                Debug.Print($"Closing connection to peripheral: {e.Message}");
                Dispose();
            }
        }

        /// <summary>
        /// Check if a particular device passes the post-discovery filters. This does not check filters implemented
        /// at the device discovery level (through DeviceWatcher selectors) such as major & minor device class.
        /// </summary>
        private bool DevicePassesFilters(DeviceInformation deviceInformation)
        {
            // Note that we don't filter out by 'IsPresentPropertyName' here because we need to return devices
            // which are paired and within discoverable range. However, 'IsPresentPropertyName' is set to False
            // for paired devices that are discovered automatically from a cache, so we ignore that property
            // and simply return all discovered devices.

            if (_filterName != null && deviceInformation.Name != _filterName)
            {
                return false; // ignore
            }
            if (_filterNamePrefix != null && !deviceInformation.Name.StartsWith(_filterNamePrefix))
            {
                return false; // ignore
            }

            return true;
        }

        #region Custom Pairing Event Handlers

        private void CustomOnPairingRequested(DeviceInformationCustomPairing sender,
            DevicePairingRequestedEventArgs args)
        {
            args.Accept(_pairingCode);
        }

        #endregion

        #region DeviceWatcher Event Handlers

        private void PeripheralDiscovered(DeviceWatcher sender, DeviceInformation deviceInformation)
        {
            // Record deviceInformation here in case a later update event causes the peripheral to pass filters.
            _discoveredPeripherals.Add(deviceInformation);
            PeripheralDiscoveredOrUpdated(deviceInformation);
        }

        /// <summary>
        /// Handle event when a discovered peripheral is updated
        /// </summary>
        /// <remarks>
        /// In addition to updating stored info, having an event handler for <see cref="DeviceWatcher.Updated"/>
        /// seems to be necessary for timely "didDiscoverPeripheral" notifications. If there is no handler, all
        /// discovered peripherals are notified right before enumeration completes.
        /// </remarks>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void PeripheralUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            var deviceInfo = _discoveredPeripherals.FirstOrDefault(peripheral => peripheral.Id == args.Id);
            if (deviceInfo == null)
            {
                Debug.Print($"Warning: received an update for unrecorded ID: {args.Id}");
                return;
            }
            deviceInfo.Update(args);
            PeripheralDiscoveredOrUpdated(deviceInfo);
        }

        private void PeripheralDiscoveredOrUpdated(DeviceInformation deviceInformation)
        {
            deviceInformation.Properties.TryGetValue(BluetoothAddressPropertyName, out var address);
            deviceInformation.Properties.TryGetValue(SignalStrengthPropertyName, out var rssi);
            var peripheralId = ((string)address)?.Replace(":", "");

            var peripheralInfo = new JObject
            {
                new JProperty("peripheralId", peripheralId),
                new JProperty("name", new JValue(deviceInformation.Name)),
                new JProperty("rssi", rssi)
            };

            if (DevicePassesFilters(deviceInformation))
            {
                SendRemoteRequest("didDiscoverPeripheral", peripheralInfo);
            }
        }

        private void EnumerationCompleted(DeviceWatcher sender, object args)
        {
            Debug.Print("Enumeration completed.");
        }

        private void EnumerationStopped(DeviceWatcher sender, object args)
        {
            if (_watcher.Status == DeviceWatcherStatus.Aborted)
            {
                Debug.Print("Enumeration stopped unexpectedly.");
            }
            else if (_watcher.Status == DeviceWatcherStatus.Stopped)
            {
                Debug.Print("Enumeration stopped.");
            }
            _watcher.Added -= PeripheralDiscovered;
            _watcher.EnumerationCompleted -= EnumerationCompleted;
            _watcher.Updated -= PeripheralUpdated;
            _watcher.Stopped -= EnumerationStopped;
            _watcher = null;
        }

        #endregion
    }
}
