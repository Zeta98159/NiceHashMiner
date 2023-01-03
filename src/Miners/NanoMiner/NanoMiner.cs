﻿using Newtonsoft.Json;
using NHM.Common;
using NHM.Common.Enums;
using NHM.MinerPlugin;
using NHM.MinerPluginToolkitV1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NHM.MinerPluginToolkitV1.Configs;

namespace NanoMiner
{
    public class NanoMiner : MinerBase, IDisposable
    {

        private readonly HttpClient _httpClient = new HttpClient();

        protected readonly Dictionary<string, int> _mappedDeviceIds = new Dictionary<string, int>();

        public NanoMiner(string uuid, Dictionary<string, int> mappedDeviceIds) : base(uuid)
        {
            _mappedDeviceIds = mappedDeviceIds;
        }

        protected override IEnumerable<MiningPair> GetSortedMiningPairs(IEnumerable<MiningPair> miningPairs)
        {
            var pairsList = miningPairs.ToList();
            // sort by mapped ids
            pairsList.Sort((a, b) => _mappedDeviceIds[a.Device.UUID].CompareTo(_mappedDeviceIds[b.Device.UUID]));
            return pairsList;
        }

        protected override void Init()
        {
            // separator ","
            if (_algorithmType != AlgorithmType.VerusHash)
                _devices = string.Join(MinerCommandLineSettings.DevicesSeparator, _miningPairs.Select(p => _mappedDeviceIds[p.Device.UUID]));
        }

        public async override Task<ApiData> GetMinerStatsDataAsync()
        {
            var api = new ApiData();
            try
            {
                var result = await _httpClient.GetStringAsync($"http://127.0.0.1:{_apiPort}/stats");
                api.ApiResponse = result;
                var apiResponse = JsonConvert.DeserializeObject<JsonApiResponse>(result);

                if (apiResponse.Algorithms.FirstOrDefault().ContainsKey("Verushash")){
                    var parsedApiResponse = JsonApiHelpers.ParseCPUJsonApiResponse(apiResponse);
                    var totalSpeed = 0d;
                    var perDeviceSpeedInfo = new Dictionary<string, IReadOnlyList<(AlgorithmType type, double speed)>>();
                    var perDevicePowerInfo = new Dictionary<string, int>();
                    // init per device sums
                    foreach (var pair in _miningPairs)
                    {
                        var uuid = pair.Device.UUID;
                        var currentSpeed = parsedApiResponse.Hashrate;  
                        totalSpeed += currentSpeed;
                        perDeviceSpeedInfo.Add(uuid, new List<(AlgorithmType type, double speed)>() { (_algorithmType, currentSpeed * (1 - DevFee * 0.01)) });
                        // no power usage info
                        perDevicePowerInfo.Add(uuid, -1);
                    }

                    api.AlgorithmSpeedsPerDevice = perDeviceSpeedInfo;
                    api.PowerUsagePerDevice = perDevicePowerInfo;
                    api.PowerUsageTotal = -1;
                }
                else
                {
                    var parsedApiResponse = JsonApiHelpers.ParseJsonApiResponse(apiResponse, _mappedDeviceIds);

                    var perDeviceSpeedInfo = new Dictionary<string, IReadOnlyList<(AlgorithmType type, double speed)>>();
                    var perDevicePowerInfo = new Dictionary<string, int>();
                    var totalSpeed = 0d;
                    var totalPowerUsage = 0;

                    foreach (var miningPair in _miningPairs)
                    {
                        var deviceUUID = miningPair.Device.UUID;
                        if (parsedApiResponse.ContainsKey(deviceUUID))
                        {
                            var stat = parsedApiResponse[deviceUUID];
                            var currentPower = (int)stat.Power;
                            totalPowerUsage += currentPower;
                            var hashrate = stat.Hashrate * (1 - DevFee * 0.01);
                            totalSpeed += hashrate;
                            perDeviceSpeedInfo.Add(deviceUUID, new List<(AlgorithmType type, double speed)>() { (_algorithmType, hashrate) });
                            perDevicePowerInfo.Add(deviceUUID, currentPower);
                        }
                        else
                        {
                            perDeviceSpeedInfo.Add(deviceUUID, new List<(AlgorithmType type, double speed)>() { (_algorithmType, 0) });
                            perDevicePowerInfo.Add(deviceUUID, 0);
                        }
                    }

                    api.AlgorithmSpeedsPerDevice = perDeviceSpeedInfo;
                    api.PowerUsagePerDevice = perDevicePowerInfo;
                    api.PowerUsageTotal = totalPowerUsage;
                }
            }
            catch (Exception e)
            {
                Logger.Error(_logGroup, $"Error occured while getting API stats: {e.Message}");
            }

            return api;
        }

        public override async Task<BenchmarkResult> StartBenchmark(CancellationToken stop, BenchmarkPerformanceType benchmarkType = BenchmarkPerformanceType.Standard)
        {
            using var tickCancelSource = new CancellationTokenSource();
            // determine benchmark time 
            // settup times
            int benchmarkTime;
            var isDaggerNvidia = _miningPairs.Any(mp => mp.Algorithm.FirstAlgorithmType == AlgorithmType.DaggerHashimoto) && _miningPairs.Any(mp => mp.Device.DeviceType == DeviceType.NVIDIA);
            if (_miningPairs.Any(mp => mp.Algorithm.FirstAlgorithmType == AlgorithmType.KAWPOW))
            {
                benchmarkTime = MinerBenchmarkTimeSettings.ParseBenchmarkTime(new List<int> { 180, 240, 300 }, MinerBenchmarkTimeSettings, _miningPairs, benchmarkType);
            }
            else
            {
                benchmarkTime = MinerBenchmarkTimeSettings.ParseBenchmarkTime(new List<int> { 60, 120, 180 }, MinerBenchmarkTimeSettings, _miningPairs, benchmarkType);
            }
            var maxTicks = MinerBenchmarkTimeSettings.ParseBenchmarkTicks(new List<int> { 1, 3, 9 }, MinerBenchmarkTimeSettings, _miningPairs, benchmarkType);
            var maxTicksEnabled = MinerBenchmarkTimeSettings.MaxTicksEnabled && !isDaggerNvidia;

            //// use demo user and disable the watchdog
            var commandLine = MiningCreateCommandLine();
            var (binPath, binCwd) = GetBinAndCwdPaths();
            Logger.Info(_logGroup, $"Benchmarking started with command: {commandLine}");
            Logger.Info(_logGroup, $"Benchmarking settings: time={benchmarkTime} ticks={maxTicks} ticksEnabled={maxTicksEnabled}");
            Logger.Info(_logGroup, $"Benchmarking is Dagger NVIDIA LHR {isDaggerNvidia}");
            var bp = new BenchmarkProcess(binPath, binCwd, commandLine, GetEnvironmentVariables());
            // disable line readings and read speeds from API
            bp.CheckData = null;

            var benchmarkTimeout = TimeSpan.FromSeconds(benchmarkTime + 5);
            var benchmarkWait = TimeSpan.FromMilliseconds(500);
            var t = MinerToolkit.WaitBenchmarkResult(bp, benchmarkTimeout, benchmarkWait, stop, tickCancelSource.Token);

            var stoppedAfterTicks = false;
            var validTicks = 0;
            var ticks = benchmarkTime / 10; // on each 10 seconds tick
            var result = new BenchmarkResult();
            var benchmarkApiData = new List<ApiData>();
            int delay = maxTicksEnabled ? (benchmarkTime / maxTicks) * 1000 : 10 * 1000;


            for (var tick = 0; tick < ticks; tick++)
            {
                if (t.IsCompleted || t.IsCanceled || stop.IsCancellationRequested) break;
                await Task.Delay(delay, stop); // 10 seconds delay
                if (t.IsCompleted || t.IsCanceled || stop.IsCancellationRequested) break;

                var ad = await GetMinerStatsDataAsync();
                var adTotal = ad.AlgorithmSpeedsTotal();
                var isTickValid = adTotal.Count > 0 && adTotal.All(pair => pair.speed > 0);
                benchmarkApiData.Add(ad);
                if (isTickValid) ++validTicks;
                if (maxTicksEnabled && validTicks >= maxTicks)
                {
                    stoppedAfterTicks = true;
                    break;
                }
            }
            // await benchmark task
            if (stoppedAfterTicks)
            {
                try
                {
                    tickCancelSource.Cancel();
                }
                catch
                { Logger.Error(_logGroup, "tickCancelSource error"); }
            }
            await t;
            if (stop.IsCancellationRequested)
            {
                return t.Result;
            }

            // calc speeds
            // TODO calc std deviaton to reduce invalid benches
            try
            {
                var nonZeroSpeeds = benchmarkApiData.Where(ad => ad.AlgorithmSpeedsTotal().Count > 0 && ad.AlgorithmSpeedsTotal().All(pair => pair.speed > 0))
                                                    .Select(ad => (ad, ad.AlgorithmSpeedsTotal().Count)).ToList();
                var speedsFromTotals = new List<(AlgorithmType type, double speed)>();
                if (nonZeroSpeeds.Count > 0)
                {
                    var maxAlgoPiarsCount = nonZeroSpeeds.Select(adCount => adCount.Count).Max();
                    var sameCountApiDatas = nonZeroSpeeds.Where(adCount => adCount.Count == maxAlgoPiarsCount).Select(adCount => adCount.ad).ToList();
                    var firstPair = sameCountApiDatas.FirstOrDefault();
                    var speedSums = firstPair.AlgorithmSpeedsTotal().Select(pair => new KeyValuePair<AlgorithmType, double>(pair.type, 0.0)).ToDictionary(x => x.Key, x => x.Value);
                    // sum 
                    foreach (var ad in sameCountApiDatas)
                    {
                        foreach (var pair in ad.AlgorithmSpeedsTotal())
                        {
                            speedSums[pair.type] += pair.speed;
                        }
                    }
                    // average
                    foreach (var algoId in speedSums.Keys.ToArray())
                    {
                        speedSums[algoId] /= sameCountApiDatas.Count;
                    }
                    result = new BenchmarkResult
                    {
                        AlgorithmTypeSpeeds = firstPair.AlgorithmSpeedsTotal().Select(pair => (pair.type, speedSums[pair.type])).ToList(),
                        Success = true
                    };
                }
            }
            catch (Exception e)
            {
                Logger.Warn(_logGroup, $"benchmarking AlgorithmSpeedsTotal error {e.Message}");
            }
            // return API result
            return result;
        }
        private bool _disposed = false;
        public virtual void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                try
                {
                    _httpClient.Dispose();
                }
                catch (Exception) { }
            }
            _disposed = true;
        }
        ~NanoMiner()
        {
            Dispose(false);
        }
    }
}
