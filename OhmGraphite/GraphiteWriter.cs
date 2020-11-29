﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using static System.FormattableString;

namespace OhmGraphite
{
    public class GraphiteWriter : IWriteMetrics
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private readonly string _localHost;
        private readonly string _remoteHost;
        private readonly int _remotePort;
        private readonly bool _tags;
        private TcpClient _client = new TcpClient();
        private bool _failure = true;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public GraphiteWriter(string remoteHost, int remotePort, string localHost, bool tags)
        {
            _remoteHost = remoteHost;
            _remotePort = remotePort;
            _tags = tags;
            _localHost = localHost;
        }

        public async Task ReportMetrics(DateTime reportTime, IEnumerable<ReportedValue> sensors)
        {
            // Since the graphite writer keeps the same connection open across
            // writes, we need to ensure that only one thread has access to
            // the connection at a time. Multiple threads can be in this
            // method when the time it takes to poll and write the data is
            // longer than the interval time. However we don't want an
            // unbounded number of threads stuck waiting to write, so
            // jettison any attempt after waiting for more than a second.
            if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                throw new ApplicationException("unable to acquire lock on graphite connection");
            }

            try
            {
                await SendGraphite(reportTime, sensors);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task SendGraphite(DateTime reportTime, IEnumerable<ReportedValue> sensors)
        {
            try
            {
                // Reconnect whenever the previous network attempt failed or first
                // time connections
                if (_failure || !_client.Connected)
                {
                    _client.Close();
                    _client = new TcpClient();
                    Logger.Debug($"New connection to {_remoteHost}:{_remotePort}");
                    await _client.ConnectAsync(_remoteHost, _remotePort);
                }

                // We don't want to transmit metrics across multiple seconds as they
                // are being retrieved so calculate the timestamp of the signaled event
                // only once.
                long epoch = new DateTimeOffset(reportTime).ToUnixTimeSeconds();

                // Create a stream writer that leaves the underlying stream open
                // when the writer is closed, as we don't want our TCP connection
                // closed too. Since this requires the four param constructor for
                // the stream writer, the encoding and buffer sized are copied from
                // the C# reference source.
                using (var writer = new StreamWriter(_client.GetStream(), Utf8NoBom, bufferSize: 1024, leaveOpen: true))
                {
                    foreach (var sensor in sensors)
                    {
                        await writer.WriteLineAsync(FormatGraphiteData(epoch, sensor));
                    }
                }

                await _client.GetStream().FlushAsync();
                _failure = false;
            }
            catch (SocketException)
            {
                _failure = true;
                throw;
            }
        }

        private static string NormalizedIdentifier(string host, ReportedValue sensor)
        {
            // Take the sensor's identifier (eg. /nvidiagpu/0/load/0)
            // and transform into nvidiagpu.0.load.<name> where <name>
            // is the name of the sensor lowercased with spaces removed.
            // A name like "GPU Core" is turned into "gpucore". Also
            // since some names are like "cpucore#2", turn them into
            // separate metrics by replacing "#" with "."
            string identifier = sensor.Identifier.Replace('/', '.').Substring(1);
            identifier = identifier.Remove(identifier.LastIndexOf('.')).Replace("{", null).Replace("}", null);
            string name = sensor.Sensor.ToLower().Replace(" ", null).Replace('#', '.');
            return $"ohm.{host}.{identifier}.{name}";
        }

        public static string GraphiteEscape(string src)
        {
            // Formula for escaping graphite data is taken from
            // collectd's utils_format_graphite.c
            var builder = new StringBuilder(src.Length);
            foreach (char c in src)
            {
                if (c == '.' || char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    builder.Append('-');
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        public string FormatGraphiteData(long epoch, ReportedValue data)
        {
            // Graphite API wants <metric> <value> <timestamp>. We prefix the metric
            // with `ohm` as to not overwrite potentially existing metrics
            string id = NormalizedIdentifier(_localHost, data);

            if (!_tags)
            {
                return Invariant($"{id} {data.Value} {epoch:d}");
            }

            return $"{id};" +
                   $"host={_localHost};" +
                   "app=ohm;" +
                   $"hardware={GraphiteEscape(data.Hardware)};" +
                   $"hardware_type={Enum.GetName(typeof(HardwareType), data.HardwareType)};" +
                   $"sensor_type={Enum.GetName(typeof(SensorType), data.SensorType)};" +
                   $"sensor_index={data.SensorIndex};" +
                   $"raw_name={GraphiteEscape(data.Sensor)} " +
                   Invariant($"{data.Value} {epoch:d}");
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}