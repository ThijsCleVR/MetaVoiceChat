using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace MetaVoiceChat.Input.Mic
{
    public class VcMic : IDisposable
    {
        private readonly MonoBehaviour coroutineProvider;
        private readonly int samplesPerFrame;

        public bool IsRecording { get; private set; } = false;

        public AudioClip AudioClip { get; private set; }

        public string[] Devices => Microphone.devices;
        public string SelectedDevice { get; private set; } = null;
        public string ActiveDevice { get; private set; } = null;

        private int currentFrameIndex = 0;

        private Coroutine recordCoroutine;

        public event Action<int, float[]> OnFrameReady;
        public event Action<string> OnActiveDeviceChanged;

        public VcMic(MonoBehaviour coroutineProvider, int samplesPerFrame)
        {
            this.coroutineProvider = coroutineProvider;
            this.samplesPerFrame = samplesPerFrame;
        }

        public void SetSelectedDevice(string device)
        {
            if (device == SelectedDevice)
            {
                return;
            }

            SelectedDevice = device;

            if (IsRecording)
            {
                StartRecording();
            }
        }

        public bool StartRecording()
        {
            StopRecording();

            if (Devices.Length <= 0)
            {
                Debug.LogWarning("No microphone detected for voice chat!");
                return false;
            }

            if (!Devices.Contains(SelectedDevice))
            {
                ActiveDevice = Devices[0];
            }
            else
            {
                ActiveDevice = SelectedDevice;
            }

            OnActiveDeviceChanged?.Invoke(ActiveDevice);

            AudioClip = Microphone.Start(ActiveDevice, true, VcConfig.ClipLoopSeconds, VcConfig.SamplesPerSecond);

            if (AudioClip == null)
            {
                Debug.LogWarning("Microphone failed to start recording for voice chat!");

                StopRecording();
                return false;
            }

            if (AudioClip.channels != 1)
            {
                Debug.LogWarning("Microphone must have exactly one channel for voice chat!");

                StopRecording();
                return false;
            }

            recordCoroutine = coroutineProvider.StartCoroutine(CoRecord());

            IsRecording = true;

            return true;
        }

        public void StopRecording()
        {
            if (recordCoroutine != null)
            {
                coroutineProvider.StopCoroutine(recordCoroutine);
                recordCoroutine = null;
            }

            IsRecording = false;

            if (Microphone.IsRecording(ActiveDevice))
            {
                Microphone.End(ActiveDevice);
            }

            UnityEngine.Object.Destroy(AudioClip);
            AudioClip = null;

            if (ActiveDevice != null)
            {
                ActiveDevice = null;
                OnActiveDeviceChanged?.Invoke(ActiveDevice);
            }
        }

        private IEnumerator CoRecord()
        {
            AudioClip clip = this.AudioClip;
            string device = this.ActiveDevice;

            if (clip == null)
            {
                yield break;
            }

            int clipSampleCount = clip.samples;

            if (this.samplesPerFrame <= 0 || this.samplesPerFrame > clipSampleCount)
            {
                this.StopRecording();
                yield break;
            }

            float[] frameBuffer = new float[this.samplesPerFrame];

            int wrapCount = 0;
            int prevMicPos = 0;
            int readAbsPos = Microphone.GetPosition(device);

            // Tune this based on how much main-thread time you can spend per frame.
            const int MaxFramesPerTick = 8;

            while (this.AudioClip != null && Microphone.IsRecording(device))
            {
                int micPos = Microphone.GetPosition(device);

                if (micPos < prevMicPos)
                {
                    wrapCount++;
                }

                prevMicPos = micPos;

                int writeAbsPos = (wrapCount * clipSampleCount) + micPos;
                int unreadSampleCount = writeAbsPos - readAbsPos;

                if (unreadSampleCount >= this.samplesPerFrame)
                {
                    int framesReady = unreadSampleCount / this.samplesPerFrame;

                    // If we fell too far behind, drop older frames instead of stalling the main thread.
                    if (framesReady > MaxFramesPerTick)
                    {
                        framesReady = MaxFramesPerTick;
                        readAbsPos = writeAbsPos - (framesReady * this.samplesPerFrame);

                        // Keep alignment stable.
                        readAbsPos -= readAbsPos % this.samplesPerFrame;
                    }

                    for (int n = 0; n < framesReady; n++)
                    {
                        int readOffset = readAbsPos % clipSampleCount;

                        clip.GetData(frameBuffer, readOffset);
                        this.OnFrameReady?.Invoke(this.currentFrameIndex, frameBuffer);
                        this.currentFrameIndex++;

                        readAbsPos += this.samplesPerFrame;
                    }
                }

                yield return null;
            }

            this.StopRecording();
        }

        //private IEnumerator CoRecord()
        //{
        //    int wrapCount = 0;
        //    int readAbsPos = 0;
        //    int prevPos = 0;
        //    float[] samples = new float[samplesPerFrame];

        //    while (AudioClip != null && Microphone.IsRecording(ActiveDevice))
        //    {
        //        int currPos = Microphone.GetPosition(ActiveDevice);

        //        if (currPos < prevPos)
        //        {
        //            wrapCount++;
        //        }

        //        prevPos = currPos;

        //        int currAbsPos = (wrapCount * AudioClip.samples) + currPos;

        //        // Optional safety cap so one frame cannot spend forever catching up
        //        int maxFramesToProcessThisTick = 4;
        //        int processed = 0;

        //        while (processed < maxFramesToProcessThisTick &&
        //               readAbsPos + samples.Length < currAbsPos)
        //        {
        //            int offsetSamples = readAbsPos % AudioClip.samples;
        //            AudioClip.GetData(samples, offsetSamples);

        //            OnFrameReady?.Invoke(NextFrameIndex, samples);

        //            readAbsPos += samples.Length;
        //            processed++;
        //        }

        //        yield return null;
        //    }

        //    StopRecording();
        //}

        //private IEnumerator CoRecord()
        //{
        //    int i = 0;
        //    int readAbsPos = 0;
        //    int prevPos = 0;
        //    float[] samples = new float[samplesPerFrame];

        //    while (AudioClip != null && Microphone.IsRecording(ActiveDevice))
        //    {
        //        bool isNewDataAvailable = true;

        //        while (isNewDataAvailable)
        //        {
        //            int currPos = Microphone.GetPosition(ActiveDevice);
        //            if (currPos < prevPos)
        //            {
        //                i++;
        //            }

        //            prevPos = currPos;

        //            int currAbsPos = i * AudioClip.samples + currPos;
        //            int nextReadAbsPos = readAbsPos + samples.Length;

        //            if (nextReadAbsPos < currAbsPos)
        //            {
        //                // A possible optimization is to allocate a larger fixed sized pooled array
        //                // Allocate the array size by the number of samples that are ready to read
        //                // Read these all at once instead of using multiple AudioClip.GetData() calls

        //                int offsetSamples = readAbsPos % AudioClip.samples;
        //                AudioClip.GetData(samples, offsetSamples);

        //                int index = NextFrameIndex;
        //                OnFrameReady?.Invoke(index, samples);

        //                readAbsPos = nextReadAbsPos;
        //                isNewDataAvailable = true;
        //            }
        //            else
        //            {
        //                isNewDataAvailable = false;
        //            }
        //        }

        //        yield return null;
        //    }

        //    StopRecording();
        //}

        public void Dispose()
        {
            StopRecording();
        }
    }
}