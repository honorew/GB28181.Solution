﻿//-----------------------------------------------------------------------------
// Filename: RTPFrame.cs
//
// Description: Represents a series of RTP packets that combine together to make a single media frame.
//
// History:
// 29 Jan 2014	Aaron Clauson	Created.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//

using GB28181.Net.RTP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GB28181.Net
{
    public class RTPFrame
    {
        public uint Timestamp { get; set; }
        public bool HasMarker { get; set; }
        public bool HasBeenProcessed { get; set; }
        //public int FrameHeaderLength = 0;   // Some media types, such as VP8 video, have a header at the start of each RTP data payload. It needs to be stripped.
        public FrameTypesEnum FrameType { get; set; }

        public List<RTPPacket> FramePackets { get; } = new List<RTPPacket>();

        /// <summary>
        /// 包数量
        /// </summary>
        public int Count
        {
            get
            {
                return FramePackets.Count;
            }
        }

        public uint StartSequenceNumber
        {
            get
            {
                var startPacket = FramePackets.OrderBy(x => x.Header.SequenceNumber).FirstOrDefault();

                if (startPacket != null)
                {
                    return startPacket.Header.SequenceNumber;
                }
                else
                {
                    return 0;
                }
            }
        }

        public uint EndSequenceNumber
        {
            get
            {
                var finalPacket = FramePackets.OrderByDescending(x => x.Header.SequenceNumber).FirstOrDefault();

                if (finalPacket != null)
                {
                    return finalPacket.Header.SequenceNumber;
                }
                else
                {
                    return 0;
                }
            }
        }

        public RTPFrame()
        { }

        /// <summary>
        /// Audio frames are generally contained within a single RTP packet. This method is a shortcut
        /// to construct a frame from a single RTP packet.
        /// </summary>
        public static RTPFrame MakeSinglePacketFrame(RTPPacket rtpPacket)
        {
            RTPFrame frame = new RTPFrame();
            frame.AddRTPPacket(rtpPacket);
            frame.Timestamp = rtpPacket.Header.Timestamp;

            return frame;
        }

        public void AddRTPPacket(RTPPacket rtpPacket)
        {
            FramePackets.Add(rtpPacket);

            //if (HasMarker && FramePayload == null)
            //{
            //    FramePayload = IsComplete(_packets, payloadHeaderLength);
            //}
        }

        public bool IsComplete
        {
            get
            {
                if (!HasMarker)
                {
                    return false;
                }

                // The frame has the marker bit set. Check that there are no missing sequence numbers.
                uint previousSeqNum = 0;

                foreach (var rtpPacket in FramePackets.OrderBy(x => x.Header.SequenceNumber))
                {
                    if (previousSeqNum == 0)
                    {
                        previousSeqNum = rtpPacket.Header.SequenceNumber;
                        //payload.AddRange(rtpPacket.Payload.Skip(payloadHeaderLength));
                        //payloadPackets.Add(rtpPacket);
                    }
                    else if (previousSeqNum != rtpPacket.Header.SequenceNumber - 1)
                    {
                        // Missing packet.
                        return false;
                    }
                    else
                    {
                        previousSeqNum = rtpPacket.Header.SequenceNumber;
                        //payload.AddRange(rtpPacket.Payload.Skip(payloadHeaderLength));
                        //payloadPackets.Add(rtpPacket);
                    }
                }

                //return payload.ToArray();

                //return Mjpeg.ProcessMjpegFrame(payloadPackets);
                return true;
            }
        }

        //public byte[] GetFramePayload()
        //{
        //    List<byte> payload = new List<byte>();

        //    foreach (var rtpPacket in _packets.OrderBy(x => x.Header.SequenceNumber))
        //    {
        //        if (FrameType == FrameTypesEnum.VP8)
        //        {
        //            var vp8Header = RTPVP8Header.GetVP8Header(rtpPacket.Payload);
        //            payload.AddRange(rtpPacket.Payload.Skip(vp8Header.PayloadDescriptorLength));
        //        }
        //        else
        //        {
        //            payload.AddRange(rtpPacket.Payload);
        //        }
        //    }

        //    return payload.ToArray();
        //}

        public byte[] GetFramePayload()
        {
           var buffer = new System.IO.MemoryStream();
            foreach (var packet in FramePackets.OrderBy(x => x.Header.SequenceNumber))
            {
                buffer.Write(packet.Payload, 0, packet.Payload.Length);
            }
            return buffer.GetBuffer();
        }
    }
}
