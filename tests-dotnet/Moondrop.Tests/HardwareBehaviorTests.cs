using Moondrop.Core.Config;
using Moondrop.Core.Devices;
using Moondrop.Core.Protocol;
using Moondrop.Hardware;
using Moondrop.Wpf;
using System.Text;

namespace Moondrop.Tests;

[TestClass]
public sealed class HardwareBehaviorTests
{
    [TestMethod]
    public void DotNetProductionSourcesDoNotUseTaskRun()
    {
        var forbiddenCall = "Task" + ".Run";
        var sourceRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src"));
        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(forbiddenCall, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToArray();

        Assert.IsEmpty(offenders, $"{forbiddenCall} remains in: {string.Join(", ", offenders)}");
    }

    [TestMethod]
    public async Task HidSharpTransportOpensRetainedStreamOnce()
    {
        var factory = new FakeHidStreamFactory();
        using var transport = new HidSharpDawnPro2Transport("hid://selected", factory);
        factory.Stream.EnqueueRead([75, 1, 2, 3]);

        await transport.WriteAsync([1, 2, 3], TimeSpan.FromSeconds(2), CancellationToken.None);
        var read = await transport.ReadAsync(64, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.AreEqual("hid://selected", factory.OpenedPath);
        Assert.AreEqual(1, factory.OpenCount);
        Assert.AreEqual(1, factory.Stream.ReadTimeout);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, factory.Stream.Writes.Single());
        CollectionAssert.AreEqual(new byte[] { 75, 1, 2, 3 }, read.ToArray());
    }

    [TestMethod]
    public async Task HidSharpTransportAppliesNativeWriteTimeoutPerOperation()
    {
        var factory = new FakeHidStreamFactory();
        using var transport = new HidSharpDawnPro2Transport("hid://selected", factory);

        await transport.WriteAsync([1, 2, 3], TimeSpan.FromMilliseconds(37), CancellationToken.None);

        Assert.AreEqual(37, factory.Stream.WriteTimeout);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, factory.Stream.Writes.Single());
    }

    [TestMethod]
    public async Task HidSharpWriteIsTruthfullyNonCancellableAfterBlockingWriteStarts()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new FakeHidStreamFactory();
        factory.Stream.AfterWrite = cancellation.Cancel;
        using var transport = new HidSharpDawnPro2Transport("hid://selected", factory);

        await transport.WriteAsync([9, 8, 7], TimeSpan.FromSeconds(2), cancellation.Token);

        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, factory.Stream.Writes.Single());
        Assert.IsTrue(cancellation.IsCancellationRequested);
    }

    [TestMethod]
    public async Task HidSharpTransportDisposesRetainedStream()
    {
        var factory = new FakeHidStreamFactory();
        var transport = new HidSharpDawnPro2Transport("hid://selected", factory);

        await transport.DisposeAsync();

        Assert.IsTrue(factory.Stream.Disposed);
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => transport.WriteAsync([1], TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [TestMethod]
    public void HidSharpOpenByIdentityRejectsChangedPathBeforeOpeningStream()
    {
        var catalog = new FakeHidDeviceCatalog(
            new DawnPro2HidIdentity("hid://original", "35D8011D251117"));
        var factory = new FakeHidStreamFactory();
        var changed = new DawnPro2HidIdentity("hid://changed", "35D8011D251117");

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => HidSharpDawnPro2Transport.OpenByIdentity(changed, catalog, factory));

        StringAssert.Contains(error.Message, "exact pinned HID identity");
        Assert.AreEqual(0, factory.OpenCount);
    }

    [TestMethod]
    public void HidSharpCaptureIdentityRejectsAmbiguousMatchingDevices()
    {
        var catalog = new FakeHidDeviceCatalog(
            new DawnPro2HidIdentity("hid://one", "35D8011D251117"),
            new DawnPro2HidIdentity("hid://two", "35D8011D251117"));

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => HidSharpDawnPro2Transport.CaptureSingleIdentity(catalog));

        StringAssert.Contains(error.Message, "exactly one");
    }

    [TestMethod]
    public void HidSharpCaptureIdentityRejectsAbsentSerial()
    {
        var catalog = new FakeHidDeviceCatalog(new DawnPro2HidIdentity("hid://one", ""));

        var error = AssertEx.ThrowsException<InvalidOperationException>(
            () => HidSharpDawnPro2Transport.CaptureSingleIdentity(catalog));

        StringAssert.Contains(error.Message, "serial is absent");
    }

    [TestMethod]
    public async Task DeviceServiceDisposeDisposesSelectedDeviceTransport()
    {
        var transport = new FakeHidTransport();
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "Pro2", new DawnPro2Device(transport, new FakeDelay()), ""));

        await service.DisposeAsync();

        Assert.IsTrue(transport.AsyncDisposed);
    }

    [TestMethod]
    public async Task DeviceServiceRefreshTransparentlyReacquiresAPoisonedDawnPro2Device()
    {
        // A transient HID failure poisons the retained DAWN PRO2 device (SendAsync ->
        // PoisonTransport -> _poisoned = 1), after which every refresh otherwise throws
        // "Cannot access a disposed object ... DawnPro2Device" (the reported GUI crash).
        var failingTransport = new FakeHidTransport { ReadFailure = new IOException("transient HID read failure") };
        var firstDevice = new DawnPro2Device(failingTransport, new FakeDelay());
        var service = new MoondropDeviceService(
            new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "Pro2", firstDevice, ""));
        var healthyTransport = new FakeHidTransport();
        var healthyDevice = new DawnPro2Device(healthyTransport, new FakeDelay());
        service.RegisterReconnectFactory(_ => Task.FromResult<IMoondropDevice>(healthyDevice));
        await AssertEx.ThrowsExceptionAsync<IOException>(() => service.RefreshPro2Async());
        healthyTransport.EnqueueResponse(Response(3, Encoding.UTF8.GetBytes("1.5\0")));
        healthyTransport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        healthyTransport.EnqueueResponse(Response(3, DawnPro2Protocol.EncodeFixedPoint(-4)));
        healthyTransport.EnqueueResponse(Response(3, DawnPro2Protocol.EncodeFixedPoint(0)));
        for (var i = 0; i < 8; i++)
            healthyTransport.EnqueueResponse(BandReport(i));
        var snapshot = await service.RefreshPro2Async();
        Assert.AreEqual("1.5", snapshot.FirmwareVersion);
        Assert.AreEqual(DawnPro2Protocol.PeqIndex, snapshot.ActiveEq);
        Assert.AreEqual(-4.0, snapshot.PreGain, 0.0001);
        Assert.AreEqual(0.0, snapshot.GlobalGain, 0.0001);
        Assert.HasCount(8, snapshot.Bands);
        Assert.AreSame(healthyDevice, service.Device, "the service must re-select a fresh usable device after a poison");
    }
    [TestMethod]
    public async Task Pro2ReadsFirmwareActiveEqAndGainsFromExpectedOffsets()
    {
        var transport = new FakeHidTransport();
        transport.EnqueueResponse(Response(3, "1.2.3"u8.ToArray()));
        transport.EnqueueResponse(Response(3, [11]));
        transport.EnqueueResponse(Response(3, DawnPro2Protocol.EncodeFixedPoint(-5.5)));
        transport.EnqueueResponse(Response(3, DawnPro2Protocol.EncodeFixedPoint(2.25)));
        var device = new DawnPro2Device(transport, new FakeDelay());

        Assert.AreEqual("1.2.3", await device.ReadFirmwareVersionAsync());
        Assert.AreEqual(11, await device.ReadActiveEqAsync());
        Assert.AreEqual(-5.5, await device.ReadPreGainAsync());
        Assert.AreEqual(2.25, await device.ReadGlobalGainAsync());

        CollectionAssert.AreEqual(new byte[] { 75, 128, 12, 0 }, transport.Writes[0].Take(4).ToArray());
        CollectionAssert.AreEqual(new byte[] { 75, 128, 15, 0 }, transport.Writes[1].Take(4).ToArray());
        CollectionAssert.AreEqual(new byte[] { 75, 128, 35, 0 }, transport.Writes[2].Take(4).ToArray());
        CollectionAssert.AreEqual(new byte[] { 75, 128, 3, 0 }, transport.Writes[3].Take(4).ToArray());
    }

    [TestMethod]
    public async Task Pro2MalformedGainPayloadPoisonsBeforeAnotherResponseCanBeConsumed()
    {
        var transport = new FakeHidTransport();
        transport.EnqueueResponse(Response(3, DawnPro2Protocol.EncodeFixedPoint(12.5)));
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() => device.ReadPreGainAsync());
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.IsTrue(transport.Disposed);
        Assert.AreEqual(1, transport.ReadCount);
        Assert.AreEqual(1, transport.PendingResponseCount);
    }

    [TestMethod]
    public async Task Pro2MalformedActiveEqPayloadPoisonsRetainedTransport()
    {
        var transport = new FakeHidTransport();
        transport.EnqueueResponse(Response(3, [16]));
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() => device.ReadActiveEqAsync());

        Assert.IsTrue(transport.Disposed);
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());
    }

    [TestMethod]
    public async Task Pro2MalformedFirmwarePayloadPoisonsRetainedTransport()
    {
        var transport = new FakeHidTransport();
        transport.EnqueueResponse(Response(3, [0xFF, 0]));
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<InvalidDataException>(() => device.ReadFirmwareVersionAsync());

        Assert.IsTrue(transport.Disposed);
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadFirmwareVersionAsync());
    }

    [TestMethod]
    public async Task Pro2RawBandReadCapturesCompleteNormalizedPayload()
    {
        var payload = ValidRawBandPayload(2);
        payload[7] = 0xA1;
        payload[26] = 0xB2;
        payload[34] = 0xC3;
        var transport = new FakeHidTransport();
        transport.EnqueueResponse([DawnPro2Protocol.ReportId, .. payload]);
        var device = new DawnPro2Device(transport, new FakeDelay());

        var state = await device.ReadRawBandAsync(2);

        Assert.AreEqual(0xA1, state.CoefficientBytes[0]);
        Assert.AreEqual(0xB2, state.CoefficientBytes[19]);
        Assert.AreEqual(0xC3, state.Metadata34);
        CollectionAssert.AreEqual(payload, state.NormalizedPayload.ToArray());
    }

    [TestMethod]
    public async Task Pro2MalformedRawBandPayloadPoisonsBeforeAnotherResponseCanBeConsumed()
    {
        var malformed = ValidRawBandPayload(2);
        malformed[27] = 0;
        malformed[28] = 0;
        var transport = new FakeHidTransport();
        transport.EnqueueResponse([DawnPro2Protocol.ReportId, .. malformed]);
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(() => device.ReadRawBandAsync(2));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.IsTrue(transport.Disposed);
        Assert.AreEqual(1, transport.ReadCount);
        Assert.AreEqual(1, transport.PendingResponseCount);
    }

    [TestMethod]
    public async Task Pro2RawBandWriteTransmitsCanonicalSelectorsDespiteHostileCapturedSelectors()
    {
        var payload = ValidRawBandPayload(4);
        for (var index = 0; index < 20; index++)
            payload[7 + index] = (byte)(0xF0 - index);
        payload[4] = 0xA4;
        payload[34] = 0x6D;
        payload[35] = 0xB5;
        var state = DawnPro2Protocol.ParseRawBandPayload(4, payload);
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());

        await device.WriteRawBandAsync(state);

        Assert.HasCount(2, transport.Writes);
        var expected = DawnPro2Protocol.CreatePacket(DawnPro2Protocol.BuildWriteRawBandPayload(state));
        CollectionAssert.AreEqual(expected, transport.Writes[0]);
        Assert.AreEqual(4, transport.Writes[0][5]);
        Assert.AreEqual(DawnPro2Protocol.PeqIndex, transport.Writes[0][36]);
        Assert.AreEqual(0, transport.Writes[0][35]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEqCoeffToReg, transport.Writes[1][2]);
        Assert.AreEqual(4, transport.Writes[1][3]);
    }

    [TestMethod]
    public async Task Pro2RawBulkWritePrevalidatesEveryStateBeforeFirstTransmission()
    {
        var states = Enumerable.Range(0, 8)
            .Select(index => DawnPro2Protocol.ParseRawBandPayload(index, ValidRawBandPayload(index)))
            .ToArray();
        var invalidPayload = states[7].NormalizedPayload.ToArray();
        invalidPayload[27] = 0;
        invalidPayload[28] = 0;
        states[7] = new RawPeqBandState(7, invalidPayload);
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(() => device.WriteAllRawBandsAsync(states));

        Assert.IsEmpty(transport.Writes);
    }

    [TestMethod]
    public async Task Pro2MalformedResponseEnvelopeProducesDescriptiveIoError()
    {
        var transport = new FakeHidTransport();
        transport.EnqueueResponse([DawnPro2Protocol.ReportId, 1, 2]);
        var device = new DawnPro2Device(transport, new FakeDelay());

        var error = await AssertEx.ThrowsExceptionAsync<IOException>(() => device.ReadActiveEqAsync());

        StringAssert.Contains(error.Message, "expected exactly 64 bytes");
        Assert.IsTrue(transport.Disposed);
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());
    }

    [TestMethod]
    public async Task Pro2CancellationBeforeWriteLeavesTransportUsableAndEmitsNoPacket()
    {
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(() => device.ReadActiveEqAsync(cancellation.Token));

        Assert.IsEmpty(transport.Writes);
        Assert.IsFalse(transport.Disposed);
    }

    [TestMethod]
    public async Task Pro2CancellationAfterWriteDrainsResponseBeforeReportingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeHidTransport { AfterWrite = cancellation.Cancel };
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(() => device.ReadActiveEqAsync(cancellation.Token));

        Assert.AreEqual(1, transport.ReadCount);
        Assert.IsFalse(transport.LastReadCancellationToken.CanBeCanceled);
    }

    [TestMethod]
    public async Task Pro2ConcurrentReadsSerializeEachWriteWithItsOwedResponse()
    {
        var firmware = Response(3, Encoding.UTF8.GetBytes("1.5\0"));
        var activeEq = Response(3, [DawnPro2Protocol.PeqIndex]);
        var transport = new BlockingFirstReadHidTransport(firmware, activeEq);
        var device = new DawnPro2Device(transport, new FakeDelay());

        var first = device.ReadFirmwareVersionAsync();
        await transport.FirstWrite;
        var second = device.ReadActiveEqAsync();
        await Task.Yield();

        Assert.AreEqual(1, transport.WriteCount);
        Assert.IsFalse(transport.SecondWrite.IsCompleted);

        transport.ReleaseFirstRead();
        Assert.AreEqual("1.5", await first);
        Assert.AreEqual(DawnPro2Protocol.PeqIndex, await second);
        Assert.AreEqual(2, transport.WriteCount);
    }

    [TestMethod]
    public async Task Pro2ReadTimeoutPoisonsTransportBeforeAnyLateResponseCanBeReused()
    {
        var transport = new FakeHidTransport { ReadFailure = new TimeoutException("late response") };
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<TimeoutException>(() => device.ReadActiveEqAsync());
        transport.ReadFailure = null;
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.IsTrue(transport.Disposed);
        Assert.HasCount(1, transport.Writes);
        Assert.AreEqual(1, transport.ReadCount);
    }

    [TestMethod]
    public async Task Pro2AmbiguousWriteTimeoutPoisonsBeforeQueuedLateResponseCanBeReused()
    {
        var transport = new FakeHidTransport
        {
            AfterWrite = () => { },
            WriteFailure = new TimeoutException("request may have reached the device")
        };
        transport.AfterWrite = () => transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<TimeoutException>(() => device.ReadActiveEqAsync());
        transport.WriteFailure = null;
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.IsTrue(transport.Disposed);
        Assert.AreEqual(0, transport.ReadCount);
        Assert.AreEqual(1, transport.PendingResponseCount);
        Assert.HasCount(1, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2NoResponseWriteFailurePoisonsTransportBeforeAnotherCommand()
    {
        var failure = new IOException("fire-and-forget write may have reached the device");
        var transport = new FakeHidTransport { WriteFailure = failure };
        var device = new DawnPro2Device(transport, new FakeDelay());

        var error = await AssertEx.ThrowsExceptionAsync<IOException>(() => device.SaveEqToFlashAsync());
        transport.WriteFailure = null;
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.AreSame(failure, error);
        Assert.IsTrue(transport.Disposed);
        Assert.HasCount(1, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2NoResponseWriteTimeoutPoisonsTransportBeforeAnotherCommand()
    {
        var timeout = new TimeoutException("fire-and-forget write completion is ambiguous");
        var transport = new FakeHidTransport { WriteFailure = timeout };
        var device = new DawnPro2Device(transport, new FakeDelay());

        var error = await AssertEx.ThrowsExceptionAsync<TimeoutException>(() => device.SaveGainsToFlashAsync());
        transport.WriteFailure = null;
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.AreSame(timeout, error);
        Assert.IsTrue(transport.Disposed);
        Assert.HasCount(1, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2NoResponseNativeWriteCancellationPoisonsTransportBeforeAnotherCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException("native write canceled after entry", cancellation.Token);
        var transport = new FakeHidTransport { WriteFailure = failure };
        var device = new DawnPro2Device(transport, new FakeDelay());

        var error = await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(
            () => device.SaveEqToFlashAsync(cancellation.Token));
        transport.WriteFailure = null;
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.AreSame(failure, error);
        Assert.IsTrue(transport.Disposed);
        Assert.HasCount(1, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2NoResponseCancellationAfterSuccessfulWritePoisonsTransportBeforeAnotherCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var progressCount = 0;
        var transport = new FakeHidTransport { AfterWrite = cancellation.Cancel };
        var device = new DawnPro2Device(
            transport,
            new FakeDelay(),
            transactionProgress: () =>
            {
                progressCount++;
                return Task.CompletedTask;
            });

        var error = await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(
            () => device.SaveEqToFlashAsync(cancellation.Token));
        transport.AfterWrite = null;
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(1, progressCount);
        Assert.IsTrue(transport.Disposed);
        Assert.HasCount(1, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2NoResponsePostWriteProgressFailurePoisonsTransportBeforeAnotherCommand()
    {
        var progressFailure = new IOException("watchdog progress failed after write");
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(
            transport,
            new FakeDelay(),
            transactionProgress: () => Task.FromException(progressFailure));

        var error = await AssertEx.ThrowsExceptionAsync<IOException>(() => device.SaveEqToFlashAsync());
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.AreSame(progressFailure, error);
        Assert.IsTrue(transport.Disposed);
        Assert.HasCount(1, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2NoResponsePostWriteProgressCancellationPoisonsTransportBeforeAnotherCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(
            transport,
            new FakeDelay(),
            transactionProgress: () =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });

        var error = await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(
            () => device.SaveEqToFlashAsync(cancellation.Token));
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        await AssertEx.ThrowsExceptionAsync<ObjectDisposedException>(() => device.ReadActiveEqAsync());

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(transport.Disposed);
        Assert.HasCount(1, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2NoResponseCancellationBeforeWriteLeavesTransportUsableAndEmitsNoPacket()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(
            () => device.SaveEqToFlashAsync(cancellation.Token));

        Assert.IsEmpty(transport.Writes);
        Assert.IsFalse(transport.Disposed);
        transport.EnqueueResponse(Response(3, [DawnPro2Protocol.PeqIndex]));
        Assert.AreEqual(DawnPro2Protocol.PeqIndex, await device.ReadActiveEqAsync());
    }

    [TestMethod]
    public async Task RetainedHidStreamDrainsCanceledReadBeforeNextRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new FakeHidStreamFactory();
        factory.Stream.EnqueueRead(Response(3, "1.5"u8.ToArray()));
        factory.Stream.EnqueueRead(Response(3, "2.0"u8.ToArray()));
        factory.Stream.AfterWrite = cancellation.Cancel;
        using var transport = new HidSharpDawnPro2Transport("hid://selected", factory);
        var device = new DawnPro2Device(transport, new FakeDelay());

        await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(
            () => device.ReadFirmwareVersionAsync(cancellation.Token));

        factory.Stream.AfterWrite = null;
        Assert.AreEqual("2.0", await device.ReadFirmwareVersionAsync());
        Assert.AreEqual(2, factory.Stream.ReadCount);
    }

    [TestMethod]
    public async Task Pro2ReadDiagnosticsCaptureRequestCommandAndCompleteRawFrame()
    {
        var response = Response(3, [7]);
        var frames = new List<DawnPro2HidReadFrame>();
        var transport = new FakeHidTransport();
        transport.EnqueueResponse(response);
        var device = new DawnPro2Device(transport, new FakeDelay(), frames.Add);

        Assert.AreEqual(7, await device.ReadActiveEqAsync());

        Assert.HasCount(1, frames);
        Assert.AreEqual(DawnPro2Protocol.ActiveEq, frames[0].RequestCommand);
        CollectionAssert.AreEqual(response, frames[0].RawReport.ToArray());
    }

    [TestMethod]
    public async Task Pro2WriteBandThenEnableUsesPythonOrderAndDelays()
    {
        var delay = new FakeDelay();
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, delay);
        var band = new PeqBand(3, 1200, 0.9, -2.5, PeqFilterType.Peaking);

        await device.WriteBandAsync(band);

        Assert.HasCount(2, transport.Writes);
        Assert.AreEqual(DawnPro2Protocol.UpdateEq, transport.Writes[0][2]);
        Assert.AreEqual(3, transport.Writes[0][5]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEqCoeffToReg, transport.Writes[1][2]);
        Assert.AreEqual(3, transport.Writes[1][3]);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50) }, delay.Delays);
    }

    [TestMethod]
    public async Task Pro2WriteAllRejectsDuplicateIndexesBeforeAnyWrite()
    {
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());
        var duplicate = new[]
        {
            new PeqBand(2, 1000, 1, 0, PeqFilterType.Peaking),
            new PeqBand(2, 2000, 1, 0, PeqFilterType.Peaking)
        };

        await AssertEx.ThrowsExceptionAsync<ArgumentException>(() => device.WriteAllBandsAsync(duplicate));

        Assert.HasCount(0, transport.Writes);
    }

    [TestMethod]
    public async Task Pro2WriteAllPrevalidatesEveryBandBeforeAnyWrite()
    {
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());
        var bands = new[]
        {
            new PeqBand(0, 1000, 1, 0, PeqFilterType.Peaking),
            new PeqBand(1, 1000, 10, 12, PeqFilterType.LowShelf2)
        };

        await AssertEx.ThrowsExceptionAsync<ArgumentException>(() => device.WriteAllBandsAsync(bands));

        Assert.IsEmpty(transport.Writes);
    }

    [TestMethod]
    public async Task Pro2ImportPrevalidatesBandsBeforeWritingOptionalPreamp()
    {
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "DAWN PRO2", device, ""));
        var invalid = new[] { new PeqBand(0, 1000, 10, 12, PeqFilterType.LowShelf2) };

        await AssertEx.ThrowsExceptionAsync<ArgumentException>(() => service.ImportEqAsync(invalid, -2.5, applyPreamp: true));

        Assert.IsEmpty(transport.Writes);
        await service.DisposeAsync();
    }

    [TestMethod]
    public async Task Pro2SaveCommandsUseFlashDelaysWithoutImplicitImportSave()
    {
        var delay = new FakeDelay();
        var transport = new FakeHidTransport();
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "Pro2", new DawnPro2Device(transport, delay), ""));
        var bands = new[] { new PeqBand(0, 100, 1, 1, PeqFilterType.Peaking), new PeqBand(1, 200, 1, -1, PeqFilterType.Peaking) };

        await service.ImportEqAsync(bands, preamp: -3, applyPreamp: true);
        Assert.IsFalse(transport.Writes.Any(x => x[2] == DawnPro2Protocol.SaveEqToFlash));

        await service.SaveEqToFlashAsync();
        await service.SaveGainsToFlashAsync();

        Assert.AreEqual(2, transport.Writes.Count(x => x[2] == DawnPro2Protocol.UpdateEq));
        Assert.AreEqual(2, transport.Writes.Count(x => x[2] == DawnPro2Protocol.UpdateEqCoeffToReg));
        Assert.AreEqual(1, transport.Writes.Count(x => x[2] == DawnPro2Protocol.SaveEqToFlash));
        Assert.AreEqual(1, transport.Writes.Count(x => x[2] == DawnPro2Protocol.SaveOffsetToFlash));
        Assert.HasCount(7, delay.Delays);
    }

    [TestMethod]
    public async Task Pro2SuccessfulNoResponseFlashSavesRetainProgressAndDelays()
    {
        var progressCount = 0;
        var delay = new FakeDelay();
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(
            transport,
            delay,
            transactionProgress: () =>
            {
                progressCount++;
                return Task.CompletedTask;
            });

        await device.SaveEqToFlashAsync();
        await device.SaveGainsToFlashAsync();

        CollectionAssert.AreEqual(
            new[] { DawnPro2Protocol.SaveEqToFlash, DawnPro2Protocol.SaveOffsetToFlash },
            transport.Writes.Select(packet => packet[2]).ToArray());
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200) },
            delay.Delays);
        Assert.AreEqual(2, progressCount);
        Assert.IsFalse(transport.Disposed);
    }

    [TestMethod]
    public async Task Pro2ImportAppliesPreampBeforeBandsWithoutFlashSave()
    {
        var transport = new FakeHidTransport();
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "Pro2", new DawnPro2Device(transport, new FakeDelay()), ""));

        await service.ImportEqAsync([new PeqBand(4, 1000, 1, 2, PeqFilterType.Peaking)], preamp: -4.5, applyPreamp: true);

        Assert.AreEqual(DawnPro2Protocol.PreGain, transport.Writes[0][2]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEq, transport.Writes[1][2]);
        Assert.AreEqual(DawnPro2Protocol.UpdateEqCoeffToReg, transport.Writes[2][2]);
        Assert.IsFalse(transport.Writes.Any(x => x[2] == DawnPro2Protocol.SaveEqToFlash));
        Assert.IsFalse(transport.Writes.Any(x => x[2] == DawnPro2Protocol.SaveOffsetToFlash));
    }

    [TestMethod]
    public async Task Pro2ExplicitEnableCoefficientsUsesSelectedBandOnly()
    {
        var transport = new FakeHidTransport();
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "Pro2", new DawnPro2Device(transport, new FakeDelay()), ""));

        await service.EnableBandCoefficientsAsync(6);

        Assert.HasCount(1, transport.Writes);
        Assert.AreEqual(DawnPro2Protocol.UpdateEqCoeffToReg, transport.Writes[0][2]);
        Assert.AreEqual(6, transport.Writes[0][3]);
    }

    [TestMethod]
    public async Task Pro2ConfigEditsDoNotFlashUntilExplicitSaveCommands()
    {
        var transport = new FakeHidTransport();
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "Pro2", new DawnPro2Device(transport, new FakeDelay()), ""));

        await service.SetActiveEqAsync(15, save: false);
        await service.SetPreGainAsync(-18, save: false);
        await service.SetGlobalGainAsync(12, save: false);

        Assert.IsFalse(transport.Writes.Any(x => x[2] == DawnPro2Protocol.SaveEqToFlash));
        Assert.IsFalse(transport.Writes.Any(x => x[2] == DawnPro2Protocol.SaveOffsetToFlash));

        await service.SaveEqToFlashAsync();
        await service.SaveGainsToFlashAsync();
        Assert.AreEqual(1, transport.Writes.Count(x => x[2] == DawnPro2Protocol.SaveEqToFlash));
        Assert.AreEqual(1, transport.Writes.Count(x => x[2] == DawnPro2Protocol.SaveOffsetToFlash));
    }

    [TestMethod]
    public async Task LegacyControlTransfersAndFailureSemanticsMatchPython()
    {
        var transport = new FakeLegacyTransport();
        transport.EnqueueResponse([0, 0, 0, 4, 1, 2, 0]);
        transport.EnqueueResponse([0, 0, 0, 4, 1, 2, 0]);
        transport.EnqueueResponse([0, 0, 0, 4, 1, 2, 0]);
        var device = new LegacyDawnProDevice("Legacy", transport, new AppConfig(), new FakeDelay());

        Assert.AreEqual("Non-Oversampling", await device.GetFilterAsync());
        Assert.AreEqual("High", await device.GetGainAsync());
        Assert.AreEqual("Off", await device.GetLedStatusAsync());
        Assert.IsTrue(await device.SetVolumeAsync(30));

        Assert.AreEqual(0x43, transport.Transfers[0].RequestType);
        Assert.AreEqual(160, transport.Transfers[0].Request);
        Assert.AreEqual(0x09A0, transport.Transfers[0].Index);
        CollectionAssert.AreEqual(new byte[] { 0xC0, 0xA5, 0xA3 }, transport.Transfers[0].Data);
        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(new byte[] { 192, 165, 4, 0x3C })));

        transport.Fail = true;
        Assert.IsNull(await device.GetVolumeAsync());
        Assert.IsFalse(await device.SetGainAsync("High"));
    }

    [TestMethod]
    public async Task LegacySuccessfulSetIgnoresRefreshFailureLikePython()
    {
        var transport = new FakeLegacyTransport();
        transport.FailOnAttempts.Add(2);
        var device = new LegacyDawnProDevice("Legacy", transport, new AppConfig(), new FakeDelay());

        Assert.IsTrue(await device.SetVolumeAsync(30));
        Assert.AreEqual(2, transport.AttemptCount);
    }

    [TestMethod]
    public async Task LegacyVolumeReadContinuesAfterInternallyHandledRefreshFailure()
    {
        var transport = new FakeLegacyTransport();
        transport.FailOnAttempts.Add(1);
        transport.EnqueueResponse([0, 0, 0, 0, 0x3C, 0, 0]);
        var device = new LegacyDawnProDevice("Legacy", transport, new AppConfig(), new FakeDelay());

        Assert.AreEqual(30, await device.GetVolumeAsync());
        Assert.AreEqual(2, transport.AttemptCount);
    }

    [TestMethod]
    public async Task LegacyApplyDefaultsUsesSavedConfigBeforeRefresh()
    {
        var transport = new FakeLegacyTransport();
        var config = new AppConfig
        {
            DefaultSettings = new DefaultSettings
            {
                DefaultVolume = 42,
                DefaultGain = "High",
                DefaultLedStatus = "Off",
                DefaultFilter = "Non-Oversampling"
            }
        };
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.Legacy, "Legacy", new LegacyDawnProDevice("Legacy", transport, config, new FakeDelay()), ""));

        Assert.IsTrue(await service.ApplyLegacyDefaultsAsync(config));

        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(LegacyProtocol.SetVolumePayload(42))));
        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(LegacyProtocol.SetGainPayload("High"))));
        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(LegacyProtocol.SetLedPayload("Off"))));
        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(LegacyProtocol.SetFilterPayload("Non-Oversampling"))));
    }

    [TestMethod]
    public async Task LegacyExactImmediateSetPayloads()
    {
        var transport = new FakeLegacyTransport();
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.Legacy, "Legacy", new LegacyDawnProDevice("Legacy", transport, new AppConfig(), new FakeDelay()), ""));

        await service.SetLegacyVolumeAsync(60);
        await service.SetLegacyGainAsync("High");
        await service.SetLegacyLedAsync("Temporarily Off");
        await service.SetLegacyFilterAsync("Slow Roll-Off Phase Compensated");

        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(new byte[] { 192, 165, 4, 0x00 })));
        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(new byte[] { 192, 165, 2, 1 })));
        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(new byte[] { 192, 165, 6, 1 })));
        Assert.IsTrue(transport.Transfers.Any(x => x.Data.SequenceEqual(new byte[] { 192, 165, 1, 3 })));
    }

    [TestMethod]
    public async Task WrongDeviceServiceMethodsReportClearErrors()
    {
        var legacyService = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.Legacy, "Legacy", new LegacyDawnProDevice("Legacy", new FakeLegacyTransport(), new AppConfig(), new FakeDelay()), ""));
        var pro2Service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "Pro2", new DawnPro2Device(new FakeHidTransport(), new FakeDelay()), ""));

        var pro2Error = await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(() => legacyService.RefreshPro2Async());
        var legacyError = await AssertEx.ThrowsExceptionAsync<InvalidOperationException>(() => pro2Service.RefreshLegacyAsync());

        StringAssert.Contains(pro2Error.Message, "DAWN PRO2 controls");
        StringAssert.Contains(legacyError.Message, "legacy controls");
    }

    [TestMethod]
    public async Task BackendFactoryTriesPro2BeforeLegacyAndCombinesFailures()
    {
        var factory = new FakeDeviceFactory(null, new LegacyDawnProDevice("Legacy", new FakeLegacyTransport(), new AppConfig(), new FakeDelay()));
        var selection = await MoondropDeviceService.SelectAsync(factory, new AppConfig());
        Assert.AreEqual(DeviceKind.Legacy, selection.Selection.Kind);
        CollectionAssert.AreEqual(new[] { "pro2", "legacy" }, factory.Attempts);
        StringAssert.Contains(selection.Selection.CombinedErrors, "Dawn Pro 2 HID");

        var failing = new FakeDeviceFactory(null, null);
        InvalidOperationException? ex = null;
        try
        {
            await MoondropDeviceService.SelectAsync(failing, new AppConfig());
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }
        Assert.IsNotNull(ex);
        StringAssert.Contains(ex.Message, "No supported Moondrop device found.");
        StringAssert.Contains(ex.Message, "Original Dawn Pro USB");
    }

    [TestMethod]
    public void ConfigRoundTripsEveryUppercasePythonSection()
    {
        var config = new AppConfig
        {
            DeviceConstants = new DeviceConstants { DataLength = 9 },
            DeviceIdentifiers = new DeviceIdentifiers { AdditionalDeviceIds = [new AdditionalDeviceId("Alt", 1, 2)] },
            UiMetrics = new UiMetrics { WindowWidth = 777 },
            Logging = new LoggingConfig { LogFile = "x.log" }
        };
        var json = config.SaveJson();
        StringAssert.Contains(json, "\"device_constants\"");
        StringAssert.Contains(json, "\"DATA_LENGTH\": 9");
        StringAssert.Contains(json, "\"ADDITIONAL_DEVICE_IDS\"");
        var loaded = AppConfig.LoadJson(json);
        Assert.AreEqual(9, loaded.DeviceConstants.DataLength);
        Assert.AreEqual("Alt", loaded.DeviceIdentifiers.AdditionalDeviceIds[0].Name);
        Assert.AreEqual(777, loaded.UiMetrics.WindowWidth);
        Assert.AreEqual("x.log", loaded.Logging.LogFile);
    }

    [TestMethod]
    public void ConfigMissingAndUnknownSectionsUseDefaults()
    {
        var config = AppConfig.LoadJson("""{"unknown":true,"default_settings":{"DEFAULT_GAIN":"High"}}""");

        Assert.AreEqual("High", config.DefaultSettings.DefaultGain);
        Assert.AreEqual(50, config.DefaultSettings.DefaultVolume);
        Assert.AreEqual(0, config.DawnPro2Settings.DefaultEqIndex);
    }

    [TestMethod]
    public void ConfigSkipsMalformedAdditionalDeviceIdsButKeepsValidPythonCompatibleEntries()
    {
        var config = AppConfig.LoadJson("""
        {"device_identifiers":{"ADDITIONAL_DEVICE_IDS":[
          {"name":"valid","vendor_id":"4660","product_id":22136},
          {"name":"missing product","vendor_id":1},
          {"name":"bad","vendor_id":"nope","product_id":2},
          7
        ]}}
        """);

        Assert.HasCount(1, config.DeviceIdentifiers.AdditionalDeviceIds);
        Assert.AreEqual("valid", config.DeviceIdentifiers.AdditionalDeviceIds[0].Name);
        Assert.AreEqual(4660, config.DeviceIdentifiers.AdditionalDeviceIds[0].VendorId);
        Assert.AreEqual(22136, config.DeviceIdentifiers.AdditionalDeviceIds[0].ProductId);
    }

    [TestMethod]
    public async Task Pro2ViewModelLoadsSavedDefaultsWithoutWritingAndRejectsNonFiniteEditorValues()
    {
        var transport = new FakeHidTransport();
        var device = new DawnPro2Device(transport, new FakeDelay());
        var service = new MoondropDeviceService(new BackendSelection<IMoondropDevice>(DeviceKind.DawnPro2, "DAWN PRO2", device, ""));
        var config = new AppConfig
        {
            DawnPro2Settings = new DawnPro2Settings { DefaultEqIndex = 6, DefaultPreGain = -2.5, DefaultGlobalGain = 1.5 }
        };
        var model = MainViewModel.CreateHardware(service, config, configFileExists: true);

        Assert.AreEqual(6, model.ActiveEq);
        Assert.AreEqual(-2.5, model.PreGain);
        Assert.AreEqual(1.5, model.GlobalGain);
        Assert.IsEmpty(transport.Writes);

        var band = model.Bands[0];
        var originalQ = band.Q;
        var originalGain = band.Gain;
        band.Q = double.NaN;
        band.Gain = double.PositiveInfinity;
        model.PreGain = double.NaN;
        Assert.AreEqual(originalQ, band.Q);
        Assert.AreEqual(originalGain, band.Gain);
        Assert.AreEqual(-2.5, model.PreGain);

        await model.DisposeAsync();
    }

    [TestMethod]
    public void ConfigSaveContentContainsLegacyAndPro2Defaults()
    {
        var config = new AppConfig()
            .WithLegacyDefaults(61, "High", "Off", "Non-Oversampling")
            .WithDawnPro2Defaults(16, -19, 13);
        var json = config.SaveJson();

        StringAssert.Contains(json, "\"DEFAULT_VOLUME\": 60");
        StringAssert.Contains(json, "\"DEFAULT_GAIN\": \"High\"");
        StringAssert.Contains(json, "\"DEFAULT_LED_STATUS\": \"Off\"");
        StringAssert.Contains(json, "\"DEFAULT_FILTER\": \"Non-Oversampling\"");
        StringAssert.Contains(json, "\"DEFAULT_EQ_INDEX\": 15");
        StringAssert.Contains(json, "\"DEFAULT_PRE_GAIN\": -18");
        StringAssert.Contains(json, "\"DEFAULT_GLOBAL_GAIN\": 12");
    }

    private static byte[] Response(int offset, IReadOnlyList<byte> values)
    {
        var response = new byte[64];
        response[0] = DawnPro2Protocol.ReportId;
        for (var i = 0; i < values.Count; i++)
            response[offset + 1 + i] = values[i];
        return response;
    }

    private static byte[] ValidRawBandPayload(int index)
    {
        var payload = new byte[DawnPro2Protocol.PayloadLength];
        payload[4] = (byte)index;
        payload[27] = 0xE8;
        payload[28] = 0x03;
        DawnPro2Protocol.EncodeFixedPoint(1).CopyTo(payload, 29);
        payload[33] = (byte)PeqFilterType.Peaking;
        payload[35] = DawnPro2Protocol.PeqIndex;
        return payload;
    }

    private static byte[] BandReport(int index)
    {
        var report = new byte[DawnPro2Protocol.ReportLength];
        report[0] = DawnPro2Protocol.ReportId;
        ValidRawBandPayload(index).CopyTo(report, 1);
        return report;
    }
}

file sealed class FakeDelay : IDeviceDelay
{
    public List<TimeSpan> Delays { get; } = [];
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

file sealed class FakeHidTransport : IDawnPro2HidTransport
{
    private readonly Queue<IReadOnlyList<byte>> _responses = new();
    public List<byte[]> Writes { get; } = [];
    public Action? AfterWrite { get; set; }
    public int ReadCount { get; private set; }
    public CancellationToken LastReadCancellationToken { get; private set; }
    public bool Disposed { get; private set; }
    public bool AsyncDisposed { get; private set; }
    public Exception? ReadFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public int PendingResponseCount => _responses.Count;
    public void EnqueueResponse(IReadOnlyList<byte> response) => _responses.Enqueue(response);
    public Task WriteAsync(IReadOnlyList<byte> packet, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Writes.Add(packet.ToArray());
        AfterWrite?.Invoke();
        if (WriteFailure is not null)
            throw WriteFailure;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<byte>> ReadAsync(int length, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        LastReadCancellationToken = cancellationToken;
        if (ReadFailure is not null)
            throw ReadFailure;
        return Task.FromResult(_responses.Count == 0 ? (IReadOnlyList<byte>)Array.Empty<byte>() : _responses.Dequeue());
    }
    public void Dispose() => Disposed = true;
    public ValueTask DisposeAsync()
    {
        AsyncDisposed = true;
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

file sealed class BlockingFirstReadHidTransport(params IReadOnlyList<byte>[] responses) : IDawnPro2HidTransport
{
    private readonly Queue<IReadOnlyList<byte>> _responses = new(responses);
    private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readCount;

    public Task FirstWrite => _firstWrite.Task;
    public Task SecondWrite => _secondWrite.Task;
    public int WriteCount { get; private set; }

    public Task WriteAsync(IReadOnlyList<byte> packet, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCount++;
        if (WriteCount == 1)
            _firstWrite.TrySetResult();
        if (WriteCount == 2)
            _secondWrite.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<byte>> ReadAsync(int length, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _readCount) == 1)
            await _releaseFirstRead.Task.ConfigureAwait(false);
        return _responses.Dequeue();
    }

    public void ReleaseFirstRead() => _releaseFirstRead.TrySetResult();
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FakeLegacyTransport : ILegacyUsbTransport
{
    private readonly Queue<IReadOnlyList<byte>> _responses = new();
    public bool Fail { get; set; }
    public int AttemptCount { get; private set; }
    public HashSet<int> FailOnAttempts { get; } = [];
    public List<(byte RequestType, byte Request, ushort Value, ushort Index, byte[] Data)> Transfers { get; } = [];
    public void EnqueueResponse(IReadOnlyList<byte> response) => _responses.Enqueue(response);
    public Task<IReadOnlyList<byte>> ControlTransferAsync(byte requestType, byte request, ushort value, ushort index, IReadOnlyList<byte> data, CancellationToken cancellationToken)
    {
        AttemptCount++;
        if (Fail || FailOnAttempts.Contains(AttemptCount))
            throw new IOException("boom");
        Transfers.Add((requestType, request, value, index, data.ToArray()));
        var isIn = (requestType & 0x80) != 0;
        return Task.FromResult(isIn && _responses.Count != 0 ? _responses.Dequeue() : Array.Empty<byte>());
    }
    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FakeHidStreamFactory : IHidStreamFactory
{
    public FakeHidStream Stream { get; } = new();
    public string? OpenedPath { get; private set; }
    public int OpenCount { get; private set; }

    public IHidStream Open(string devicePath)
    {
        OpenedPath = devicePath;
        OpenCount++;
        return Stream;
    }
}

file sealed class FakeHidDeviceCatalog(params DawnPro2HidIdentity[] identities) : IDawnPro2HidDeviceCatalog
{
    public IReadOnlyList<DawnPro2HidIdentity> Enumerate() => identities;
}

file sealed class FakeHidStream : IHidStream
{
    private readonly Queue<byte[]> _reads = new();
    public List<byte[]> Writes { get; } = [];
    public bool Disposed { get; private set; }
    public int ReadTimeout { get; set; }
    public int WriteTimeout { get; set; }
    public int ReadCount { get; private set; }
    public Action? AfterWrite { get; set; }

    public void EnqueueRead(byte[] response) => _reads.Enqueue(response);

    public void Write(byte[] buffer)
    {
        Writes.Add(buffer);
        AfterWrite?.Invoke();
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ReadCount++;
        var response = _reads.Count == 0 ? Array.Empty<byte>() : _reads.Dequeue();
        var bytes = Math.Min(count, response.Length);
        Array.Copy(response, 0, buffer, offset, bytes);
        return bytes;
    }

    public void Dispose() => Disposed = true;
}

file sealed class FakeDeviceFactory(IMoondropDevice? pro2, IMoondropDevice? legacy) : IMoondropDeviceFactory
{
    public List<string> Attempts { get; } = [];
    public Task<IMoondropDevice> CreateDawnPro2Async(CancellationToken cancellationToken)
    {
        Attempts.Add("pro2");
        return pro2 is null ? throw new InvalidOperationException("hid missing") : Task.FromResult(pro2);
    }
    public Task<IMoondropDevice> CreateLegacyAsync(AppConfig config, CancellationToken cancellationToken)
    {
        Attempts.Add("legacy");
        return legacy is null ? throw new InvalidOperationException("usb missing") : Task.FromResult(legacy);
    }
}
