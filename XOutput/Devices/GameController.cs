﻿using System;
using System.Linq;
using System.Threading;
using XOutput.Devices.Input;
using XOutput.Devices.Input.DirectInput;
using XOutput.Devices.Mapper;
using XOutput.Devices.XInput;
using XOutput.Devices.XInput.SCPToolkit;
using XOutput.Devices.XInput.Vigem;
using XOutput.Logging;
using XOutput.Tools;

namespace XOutput.Devices
{
    /// <summary>
    /// GameController is a container for input devices, mappers and output devices.
    /// </summary>
    public sealed class GameController : IDisposable
    {
        /// <summary>
        /// Gets the output device.
        /// </summary>
        public XOutputDevice XInput => xInput;
        /// <summary>
        /// Gets the mapping of the input device.
        /// </summary>
        public InputMapper Mapper => mapper;
        /// <summary>
        /// Gets the name of the input device.
        /// </summary>
        public string DisplayName => mapper.Name;
        /// <summary>
        /// Gets the number of the controller.
        /// </summary>
        public int ControllerCount => controllerCount;
        /// <summary>
        /// Gets if any XInput emulation is installed.
        /// </summary>
        public bool HasXOutputInstalled => xOutputInterface != null;
        /// <summary>
        /// Gets if force feedback is supported.
        /// </summary>
        public bool ForceFeedbackSupported => xOutputInterface is VigemDevice;
        /// <summary>
        /// Gets the force feedback device.
        /// </summary>
        public IInputDevice ForceFeedbackDevice { get; set; }

        private static readonly ILogger logger = LoggerFactory.GetLogger(typeof(GameController));

        private readonly InputMapper mapper;
        private readonly XOutputDevice xInput;
        private readonly IXOutputInterface xOutputInterface;
        private Thread thread;
        private bool running;
        private int controllerCount = 0;
        private Nefarius.ViGEm.Client.Targets.IXbox360Controller controller;

        public GameController(InputMapper mapper)
        {
            this.mapper = mapper;
            xOutputInterface = CreateXOutput();
            xInput = new XOutputDevice(mapper);
            if (!string.IsNullOrEmpty(mapper.ForceFeedbackDevice))
            {
                var device = InputDevices.Instance.GetDevices().OfType<DirectDevice>().FirstOrDefault(d => d.UniqueId == mapper.ForceFeedbackDevice);
                if (device != null)
                {
                    ForceFeedbackDevice = device;
                }
            }
            running = false;
        }

        private IXOutputInterface CreateXOutput()
        {
            if (VigemDevice.IsAvailable())
            {
                logger.Info("ViGEm devices are used.");
                return new VigemDevice();
            }
            else if (ScpDevice.IsAvailable())
            {
                logger.Warning("SCP Toolkit devices are used.");
                return new ScpDevice();
            }
            else
            {
                logger.Error("Neither ViGEm nor SCP devices can be used.");
                return null;
            }
        }

        /// <summary>
        /// Disposes all used resources
        /// </summary>
        public void Dispose()
        {
            Stop();
            xInput?.Dispose();
            xOutputInterface?.Dispose();
        }

        /// <summary>
        /// Starts the emulation of the device
        /// </summary>
        public int Start(Action onStop = null)
        {
            if (!HasXOutputInstalled)
            {
                return 0;
            }
            controllerCount = Controllers.Instance.GetId();
            if (controller != null)
            {
                controller.FeedbackReceived -= ControllerFeedbackReceived;
            }
            if (xOutputInterface.Unplug(controllerCount))
            {
                // Wait for unplugging
                Thread.Sleep(10);
            }
            if (xOutputInterface.Plugin(controllerCount))
            {
                thread = ThreadHelper.CreateAndStart(new ThreadStartParameters {
                    Name = $"Emulated controller {controllerCount} output refresher",
                    IsBackground = true,
                    Task = () => ReadAndReportValues(onStop),
                    Error = (ex) => {
                        logger.Error("Failed to read from device", ex);
                        Stop();
                    },
                    Finally = onStop,
                });
                running = true;
                logger.Info($"Emulation started on {ToString()}.");
                if (ForceFeedbackSupported)
                {
                    logger.Info($"Force feedback mapping is connected on {ToString()}.");
                    controller = ((VigemDevice)xOutputInterface).GetController(controllerCount);
                    controller.FeedbackReceived += ControllerFeedbackReceived;
                }
            }
            else
            {
                resetId();
            }
            return controllerCount;
        }

        /// <summary>
        /// Stops the emulation of the device
        /// </summary>
        public void Stop()
        {
            if (running)
            {
                running = false;
                XInput.InputChanged -= XInputInputChanged;
                if (ForceFeedbackSupported)
                {
                    controller.FeedbackReceived -= ControllerFeedbackReceived;
                    logger.Info($"Force feedback mapping is disconnected on {ToString()}.");
                }
                xOutputInterface?.Unplug(controllerCount);
                logger.Info($"Emulation stopped on {ToString()}.");
                resetId();
                if (thread != null) {
                    ThreadHelper.StopAndWait(thread);
                }
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }

        private void ReadAndReportValues(Action onStop)
        {
            XInput.InputChanged += XInputInputChanged;
            while (running)
            {
                Thread.Sleep(100);
            }
        }

        private void XInputInputChanged(object sender, DeviceInputChangedEventArgs e)
        {
            if (!xOutputInterface.Report(controllerCount, XInput.GetValues()))
            {
                Stop();
            }
        }

        private void ControllerFeedbackReceived(object sender, Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360FeedbackReceivedEventArgs e)
        {
            ForceFeedbackDevice?.SetForceFeedback((double)e.LargeMotor / byte.MaxValue, (double)e.SmallMotor / byte.MaxValue);
        }

        private void resetId()
        {
            if (controllerCount != 0)
            {
                Controllers.Instance.DisposeId(controllerCount);
                controllerCount = 0;
            }
        }
    }
}
