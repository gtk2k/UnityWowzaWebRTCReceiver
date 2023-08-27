using Newtonsoft.Json;
using System;
using System.Collections;
using System.Linq;
using System.Threading;
using Unity.WebRTC;
using UnityEngine;
using WebSocketSharp;

public class WowzaClient : MonoBehaviour 
{
    public string sdpURL;
    public string applicationName;
    public string streamName;

    private SynchronizationContext ctx;
    private WowzaStreamInfo streamInfo;
    private RTCPeerConnection pc;
    private WebSocket ws;

    private enum Side { Local, Remote }

    private JsonSerializerSettings jsonSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    [Serializable]
    public class MomoIce
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    [Serializable]
    public class WowzaSDP
    {
        public string type;
        public string sdp;
    }

    [Serializable]
    public class WowzaIceCandidate
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    [Serializable]
    public class WowzaStreamInfo
    {
        public string applicationName;
        public string streamName;
        public string sessionId;
    }

    [Serializable]
    public class WowzaUserData
    {
        public string param1;
    }

    [Serializable]
    public class WowzaSignalingMessage
    {
        public int? status;
        public string statusDescription;
        public string direction;
        public string command;
        public WowzaStreamInfo streamInfo;
        public WowzaUserData userData;
        public WowzaSDP sdp;
        public WowzaIceCandidate[] iceCandidates;

        public RTCSessionDescription ToDesc()
        {
            return new RTCSessionDescription
            {
                type = sdp.type == "offer" ? RTCSdpType.Offer : RTCSdpType.Answer,
                sdp = sdp.sdp,
            };
        }

        public RTCIceCandidate[] ToCands()
        {
            return iceCandidates.Select(cand =>
            {
                return new RTCIceCandidate(new RTCIceCandidateInit
                {
                    candidate = cand.candidate,
                    sdpMid = cand.sdpMid,
                    sdpMLineIndex = cand.sdpMLineIndex
                });
            }).ToArray();
        }

        public static WowzaSignalingMessage FromDesc(WowzaStreamInfo streamInfo, RTCSessionDescription desc)
        {
            return new WowzaSignalingMessage
            {
                direction = "play",
                command = "sendResponse",
                streamInfo = streamInfo,
                sdp = new WowzaSDP
                {
                    type = desc.type == RTCSdpType.Offer ? "offer" : "answer",
                    sdp = desc.sdp
                }
            };
        }
    }

    private void OnEnable()
    {
        StartCoroutine(WebRTC.Update());
        ctx = SynchronizationContext.Current;

        ws = new WebSocket(sdpURL);
        ws.OnOpen += Ws_OnOpen;
        ws.OnMessage += Ws_OnMessage;
        ws.OnClose += Ws_OnClose;
        ws.OnError += Ws_OnError;
        ws.Connect();
    }

    private void OnDisable()
    {

        ws?.Close();
        ws = null;
    }

    public void Connect(string url)
    {
        Debug.Log($"Connect");
        ws = new WebSocket(url);
    }

    public void Disconnect()
    {
        pc?.Close();
        pc = null;
        ws?.Close();
        ws = null;
    }

    private void Ws_OnOpen(object sender, EventArgs e)
    {
        // Debug.Log("signaling open");
        ctx.Post(_ =>
        {
            SetupPeerConnection();
            getOffer();
        }, null);
    }

    private void getOffer()
    {
        streamInfo = new WowzaStreamInfo
        {
            applicationName = applicationName,
            streamName = streamName,
            sessionId = "[empty]"
        };
        var msg = new WowzaSignalingMessage
        {
            direction = "play",
            command = "getOffer",
            streamInfo = streamInfo
        };
        Send(msg);
    }

    private void Ws_OnMessage(object sender, MessageEventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.Log($"V:{e.Data.Replace("\n", "")}");

            var msg = JsonConvert.DeserializeObject<WowzaSignalingMessage>(e.Data);
            // Debug.Log($"Receive what:{msg.what}");
            switch (msg.command)
            {
                case "getOffer":
                    {
                        // Debug.Log($"type: {data.type}, sdp:{data.sdp}");
                        streamInfo.sessionId = msg.streamInfo.sessionId;
                        var offer = msg.ToDesc();
                        StartCoroutine(SetDescription(Side.Remote, offer));
                        break;
                    }
                case "sendResponse":
                    {
                        // Debug.Log($"RemoteIceCandidate: {data.candidate}, sdpMid:{data.sdpMid}");
                        var cands = msg.ToCands();
                        for(var i  = 0; i< cands.Length; i++)
                        {
                            pc.AddIceCandidate(cands[i]);
                        }
                        break;
                    }
            }
        }, null);
    }

    private void Ws_OnClose(object sender, CloseEventArgs e)
    {
        Debug.Log("signaling close");
        ctx.Post(_ =>
        {
            Debug.Log($"Ws_OnClose > code: {e.Code}, reason: {e.Reason}");
        }, null);
    }

    private void Ws_OnError(object sender, ErrorEventArgs e)
    {
        Debug.Log("signaling error");
        ctx.Post(_ =>
        {
            Debug.LogError(e.Message);
        }, null);
    }

    private void SetupPeerConnection()
    {
        Debug.Log($"=== SetupPeerConnection");

        RTCConfiguration config = default;
        config.iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } };

        // Debug.Log($"OnConfig");

        pc = new RTCPeerConnection(ref config);
        pc.OnIceCandidate = candidate =>
        {
            //ws.Send(WowzaSignalingMessage.FromCand(candidate));
        };
        pc.OnIceGatheringStateChange = state =>
        {
            Debug.Log($"OnIceGatheringStateChange > {state}");
        };
        pc.OnConnectionStateChange = state =>
        {
            Debug.Log($"OnConnectionStateChange > {state}");
        };
        pc.OnTrack = evt =>
        {
            Debug.Log($"OnTrack: {evt.Track.Kind}");

            if (evt.Track is VideoStreamTrack videoTrack)
            {
                Debug.Log($"OnVideoTrack");

                videoTrack.OnVideoReceived += (tex) =>
                {
                    GetComponent<Renderer>().material.mainTexture = tex;
                };
            }
        };
    }

    private IEnumerator SetDescription(Side side, RTCSessionDescription desc)
    {
        var op = side == Side.Local ? pc.SetLocalDescription(ref desc) : pc.SetRemoteDescription(ref desc);
        yield return op;
        if (op.IsError)
        {
            Debug.LogError($"Set {desc.type} Error > {op.Error.message}");
            yield break;
        }
        if (side == Side.Local)
        {
            Send(WowzaSignalingMessage.FromDesc(streamInfo, desc));
        }
        else if (desc.type == RTCSdpType.Offer)
        {
            yield return StartCoroutine(CreateDescription(RTCSdpType.Answer));
        }
    }

    private IEnumerator CreateDescription(RTCSdpType type)
    {
        // Debug.Log($"CreateAnswer");

        var op = type == RTCSdpType.Offer ? pc.CreateOffer() : pc.CreateAnswer();
        yield return op;
        if (op.IsError)
        {
            Debug.LogError($"CreateDescription Error > {op.Error.message}");

        }
        yield return StartCoroutine(SetDescription(Side.Local, op.Desc));
    }


    private void Send(WowzaSignalingMessage msg)
    {
        var data = JsonConvert.SerializeObject(msg, jsonSettings);
        Debug.Log($"A:{data}");
        ws.Send(data);
    }
}