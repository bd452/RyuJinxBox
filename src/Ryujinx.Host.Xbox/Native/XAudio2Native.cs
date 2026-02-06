using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Host.Xbox.Native
{
    /// <summary>
    /// P/Invoke declarations for XAudio2.
    /// On Xbox, XAudio2 is part of the GDK and always available.
    /// On desktop Windows, we load xaudio2_9.dll.
    /// </summary>
    internal static unsafe class XAudio2Native
    {
        private const string XAudio2Dll = "xaudio2_9.dll";

        // HRESULT XAudio2Create(IXAudio2** ppXAudio2, UINT32 Flags, XAUDIO2_PROCESSOR XAudio2Processor)
        [DllImport(XAudio2Dll, EntryPoint = "XAudio2Create")]
        internal static extern int XAudio2Create(out nint ppXAudio2, uint flags, uint processor);

        // Default processor
        internal const uint XAUDIO2_DEFAULT_PROCESSOR = 0x00000001;

        // --- IXAudio2 VTable indices ---
        // IUnknown: 0=QueryInterface, 1=AddRef, 2=Release
        // IXAudio2: 3=RegisterForCallbacks, 4=UnregisterForCallbacks,
        //           5=CreateSourceVoice, 6=CreateSubmixVoice, 7=CreateMasteringVoice,
        //           8=StartEngine, 9=StopEngine, 10=CommitChanges, 11=GetPerformanceData,
        //           12=SetDebugConfiguration
        internal const int VTable_Release = 2;
        internal const int VTable_CreateSourceVoice = 5;
        internal const int VTable_CreateMasteringVoice = 7;
        internal const int VTable_StartEngine = 8;
        internal const int VTable_StopEngine = 9;

        // --- IXAudio2SourceVoice VTable indices ---
        // IXAudio2Voice: 0=GetVoiceDetails, 1=SetOutputVoices, 2=SetEffectChain,
        //                3=EnableEffect, 4=DisableEffect, 5=GetEffectState,
        //                6=SetEffectParameters, 7=GetEffectParameters, 8=SetFilterParameters,
        //                9=GetFilterParameters, 10=SetOutputFilterParameters,
        //                11=GetOutputFilterParameters, 12=SetVolume, 13=GetVolume,
        //                14=SetChannelVolumes, 15=GetChannelVolumes, 16=SetOutputMatrix,
        //                17=GetOutputMatrix, 18=DestroyVoice
        // IXAudio2SourceVoice: 19=Start, 20=Stop, 21=SubmitSourceBuffer,
        //                      22=FlushSourceBuffers, 23=Discontinuity, 24=ExitLoop,
        //                      25=GetState, 26=SetFrequencyRatio, 27=GetFrequencyRatio,
        //                      28=SetSourceSampleRate
        internal const int VTable_Voice_SetVolume = 12;
        internal const int VTable_Voice_GetVolume = 13;
        internal const int VTable_Voice_DestroyVoice = 18;
        internal const int VTable_SourceVoice_Start = 19;
        internal const int VTable_SourceVoice_Stop = 20;
        internal const int VTable_SourceVoice_SubmitSourceBuffer = 21;
        internal const int VTable_SourceVoice_FlushSourceBuffers = 22;
        internal const int VTable_SourceVoice_GetState = 25;

        // XAUDIO2_BUFFER
        [StructLayout(LayoutKind.Sequential)]
        internal struct XAUDIO2_BUFFER
        {
            public uint Flags;
            public uint AudioBytes;
            public byte* pAudioData;
            public uint PlayBegin;
            public uint PlayLength;
            public uint LoopBegin;
            public uint LoopLength;
            public uint LoopCount;
            public nint pContext;
        }

        // XAUDIO2_VOICE_STATE
        [StructLayout(LayoutKind.Sequential)]
        internal struct XAUDIO2_VOICE_STATE
        {
            public nint pCurrentBufferContext;
            public uint BuffersQueued;
            public ulong SamplesPlayed;
        }

        // WAVEFORMATEX
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        internal const ushort WAVE_FORMAT_PCM = 0x0001;
        internal const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
        internal const uint XAUDIO2_END_OF_STREAM = 0x0040;
        internal const uint XAUDIO2_COMMIT_NOW = 0;

        // COM VTable call helpers
        internal static int ComCall(nint pInterface, int vtableIndex)
        {
            nint vtable = *(nint*)pInterface;
            nint fnPtr = *((nint*)vtable + vtableIndex);
            var fn = (delegate* unmanaged[Stdcall]<nint, int>)fnPtr;
            return fn(pInterface);
        }

        internal static int ComRelease(nint pInterface)
        {
            return ComCall(pInterface, VTable_Release);
        }

        /// <summary>
        /// Call IXAudio2::CreateMasteringVoice
        /// </summary>
        internal static int CreateMasteringVoice(
            nint pXAudio2,
            out nint ppMasteringVoice,
            uint inputChannels,
            uint inputSampleRate)
        {
            ppMasteringVoice = nint.Zero;

            nint vtable = *(nint*)pXAudio2;
            nint fnPtr = *((nint*)vtable + VTable_CreateMasteringVoice);

            // IXAudio2::CreateMasteringVoice(
            //   IXAudio2MasteringVoice** ppMasteringVoice,
            //   UINT32 InputChannels = XAUDIO2_DEFAULT_CHANNELS,
            //   UINT32 InputSampleRate = XAUDIO2_DEFAULT_SAMPLERATE,
            //   UINT32 Flags = 0,
            //   LPCWSTR szDeviceId = NULL,
            //   XAUDIO2_EFFECT_CHAIN* pEffectChain = NULL,
            //   AUDIO_STREAM_CATEGORY StreamCategory = AudioCategory_GameEffects)
            fixed (nint* pp = &ppMasteringVoice)
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, nint*, uint, uint, uint, nint, nint, int, int>)fnPtr;
                return fn(pXAudio2, pp, inputChannels, inputSampleRate, 0, nint.Zero, nint.Zero, 0);
            }
        }

        /// <summary>
        /// Call IXAudio2::CreateSourceVoice
        /// </summary>
        internal static int CreateSourceVoice(
            nint pXAudio2,
            out nint ppSourceVoice,
            WAVEFORMATEX* pSourceFormat,
            uint flags,
            float maxFrequencyRatio,
            nint pCallback)
        {
            ppSourceVoice = nint.Zero;

            nint vtable = *(nint*)pXAudio2;
            nint fnPtr = *((nint*)vtable + VTable_CreateSourceVoice);

            fixed (nint* pp = &ppSourceVoice)
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, nint*, WAVEFORMATEX*, uint, float, nint, nint, nint, int>)fnPtr;
                return fn(pXAudio2, pp, pSourceFormat, flags, maxFrequencyRatio, pCallback, nint.Zero, nint.Zero);
            }
        }

        /// <summary>
        /// Call IXAudio2SourceVoice::Start
        /// </summary>
        internal static int SourceVoiceStart(nint pSourceVoice, uint flags = 0, uint operationSet = XAUDIO2_COMMIT_NOW)
        {
            nint vtable = *(nint*)pSourceVoice;
            nint fnPtr = *((nint*)vtable + VTable_SourceVoice_Start);
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)fnPtr;
            return fn(pSourceVoice, flags, operationSet);
        }

        /// <summary>
        /// Call IXAudio2SourceVoice::Stop
        /// </summary>
        internal static int SourceVoiceStop(nint pSourceVoice, uint flags = 0, uint operationSet = XAUDIO2_COMMIT_NOW)
        {
            nint vtable = *(nint*)pSourceVoice;
            nint fnPtr = *((nint*)vtable + VTable_SourceVoice_Stop);
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)fnPtr;
            return fn(pSourceVoice, flags, operationSet);
        }

        /// <summary>
        /// Call IXAudio2SourceVoice::SubmitSourceBuffer
        /// </summary>
        internal static int SourceVoiceSubmitSourceBuffer(nint pSourceVoice, XAUDIO2_BUFFER* pBuffer)
        {
            nint vtable = *(nint*)pSourceVoice;
            nint fnPtr = *((nint*)vtable + VTable_SourceVoice_SubmitSourceBuffer);
            var fn = (delegate* unmanaged[Stdcall]<nint, XAUDIO2_BUFFER*, nint, int>)fnPtr;
            return fn(pSourceVoice, pBuffer, nint.Zero);
        }

        /// <summary>
        /// Call IXAudio2SourceVoice::GetState
        /// </summary>
        internal static void SourceVoiceGetState(nint pSourceVoice, out XAUDIO2_VOICE_STATE pVoiceState, uint flags = 0)
        {
            pVoiceState = default;
            nint vtable = *(nint*)pSourceVoice;
            nint fnPtr = *((nint*)vtable + VTable_SourceVoice_GetState);
            fixed (XAUDIO2_VOICE_STATE* pState = &pVoiceState)
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, XAUDIO2_VOICE_STATE*, uint, void>)fnPtr;
                fn(pSourceVoice, pState, flags);
            }
        }

        /// <summary>
        /// Call IXAudio2SourceVoice::SetVolume
        /// </summary>
        internal static int VoiceSetVolume(nint pVoice, float volume, uint operationSet = XAUDIO2_COMMIT_NOW)
        {
            nint vtable = *(nint*)pVoice;
            nint fnPtr = *((nint*)vtable + VTable_Voice_SetVolume);
            var fn = (delegate* unmanaged[Stdcall]<nint, float, uint, int>)fnPtr;
            return fn(pVoice, volume, operationSet);
        }

        /// <summary>
        /// Call IXAudio2Voice::DestroyVoice
        /// </summary>
        internal static void VoiceDestroy(nint pVoice)
        {
            nint vtable = *(nint*)pVoice;
            nint fnPtr = *((nint*)vtable + VTable_Voice_DestroyVoice);
            var fn = (delegate* unmanaged[Stdcall]<nint, void>)fnPtr;
            fn(pVoice);
        }

        /// <summary>
        /// Call IXAudio2SourceVoice::FlushSourceBuffers
        /// </summary>
        internal static int SourceVoiceFlushSourceBuffers(nint pSourceVoice)
        {
            nint vtable = *(nint*)pSourceVoice;
            nint fnPtr = *((nint*)vtable + VTable_SourceVoice_FlushSourceBuffers);
            var fn = (delegate* unmanaged[Stdcall]<nint, int>)fnPtr;
            return fn(pSourceVoice);
        }
    }
}
