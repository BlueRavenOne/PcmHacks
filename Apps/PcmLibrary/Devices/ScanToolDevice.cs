﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PcmHacking
{
    /// <summary>
    /// This class encapsulates all code that is unique to the ScanTool MX interface.
    /// </summary>
    public class ScanToolDevice : SerialDevice
    {
        /// <summary>
        /// Device type for use in the Device Picker dialog box, and for internal comparisons.
        /// </summary>
        public const string DeviceType = "ObdLink or AllPro";

        /// <summary>
        /// Timeout periods vary depending on the current usage scenario.
        /// This indicates which scenariow was configured most recently.
        /// </summary>
        private TimeoutScenario currentTimeout = TimeoutScenario.Undefined;

        /// <summary>
        /// The device can cache the message header to speed up serial communications. 
        /// To use that properly, we need to keep track of the cached header.
        /// </summary>
        private string currentHeader = "unset";

        /// <summary>
        /// Constructor.
        /// </summary>
        public ScanToolDevice(IPort port, ILogger logger) : base(port, logger)
        {
            // Both of these numbers could be slightly larger, but round numbers are easier to work with,
            // and these are only used with the Scantool SX interface anyhow. If we detect an AllPro
            // adapter we'll overwrite these values, see the Initialize method below.

            // Please keep the left side easy to read in hex. Then add 12 bytes for VPW overhead.
            this.MaxSendSize = 192 + 12;

            // The ScanTool SX will download 512kb in roughly 30 minutes at 500 bytes per read.
            // ScanTool reliability suffers at 508 bytes or more, so we're going with a number
            // that's round in base 10 rather than in base 2.
            this.MaxReceiveSize = 500 + 12;   
            this.Supports4X = false;
        }

        /// <summary>
        /// This string is what will appear in the drop-down list in the UI.
        /// </summary>
        public override string GetDeviceType()
        {
            return DeviceType;
        }

        /// <summary>
        /// Configure the device for use - and also confirm that the device is what we think it is.
        /// </summary>
        public override async Task<bool> Initialize()
        {
            this.Logger.AddDebugMessage("Initializing " + this.ToString());

            // We're going to reset the interface device, which means that it's going
            // to forgot what header the app previously told it to use. That requires
            // the app to forget what header the interface was told to use - that will
            // cause the app to send another set-header command later on.
            this.currentHeader = "header not yet set";
            
            SerialPortConfiguration configuration = new SerialPortConfiguration();
            configuration.BaudRate = 115200;
            configuration.Timeout = 1200;

            await this.Port.OpenAsync(configuration);
            await this.Port.DiscardBuffers();

            try
            {
                // Reset
                await this.SendRequest(""); // send a cr/lf to prevent the ATZ failing.
                this.Logger.AddDebugMessage(await this.SendRequest("AT Z"));  // reset
                this.Logger.AddDebugMessage(await this.SendRequest("AT E0")); // disable echo
                this.Logger.AddDebugMessage(await this.SendRequest("AT S0")); // no spaces on responses

                // Device Identification
                string elmID = await this.SendRequest("AT I");                // Identify (ELM)
                string stID = await this.SendRequest("ST I");                 // Identify (ScanTool.net)
                string apID = await this.SendRequest("AT #1");                // Identify (AllPro)
                if (elmID != "?")
                {
                    this.Logger.AddUserMessage("Elm ID: " + elmID);
                    if (elmID.Contains("ELM327 v1.5"))
                    {
                        // TODO: Add a URL to a web page with a list of supported devices.
                        // No such web page exists yet, but I'm sure we'll create one some day...
                        this.Logger.AddUserMessage("ERROR: This OBD2 interface is not supported.");
                        return false;
                    }
                }

                if (stID != "?")
                {
                    this.Logger.AddUserMessage("ScanTool ID: " + stID);
                }

                if (apID != "?")
                {
                    this.Logger.AddUserMessage("All Pro ID: " + apID);
                    this.Logger.AddUserMessage("All Pro self test result: " + await this.SendRequest("AT #3"));  // self test
                    this.Logger.AddUserMessage("All Pro firmware: " + await this.SendRequest("AT @1"));          // firmware check

//                    this.Supports4X = true;
//                    this.MaxSendSize = 2048 + 12;
//                    this.MaxReceiveSize = 2048 + 12;
                }

                string voltage = await this.SendRequest("AT RV");             // Get Voltage
                this.Logger.AddUserMessage("Voltage: " + voltage);

                if (!await this.SendAndVerify("AT AL", "OK") ||               // Allow Long packets
                    !await this.SendAndVerify("AT SP2", "OK") ||              // Set Protocol 2 (VPW)
                    !await this.SendAndVerify("AT DP", "SAE J1850 VPW") ||    // Get Protocol (Verify VPW)
                    !await this.SendAndVerify("AT AR", "OK") ||               // Turn Auto Receive on (default should be on anyway)
                    !await this.SendAndVerify("AT AT0", "OK") ||              // Disable adaptive timeouts
                    !await this.SendAndVerify("AT SR " + DeviceId.Tool.ToString("X2"), "OK") || // Set receive filter to this tool ID
                    !await this.SendAndVerify("AT H1", "OK") ||               // Send headers
                    !await this.SendAndVerify("AT ST 20", "OK")               // Set timeout (will be adjusted later, too)                 
                    )
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                this.Logger.AddDebugMessage("Unable to initalize " + this.ToString());
                this.Logger.AddDebugMessage(exception.ToString());
                return false;
            }

            return true;
        }

        /// <summary>
        /// Set the amount of time that we'll wait for a message to arrive.
        /// </summary>
        public override async Task SetTimeout(TimeoutScenario scenario)
        {
            if (this.currentTimeout == scenario)
            {
                return;
            }

            int milliseconds = this.GetVpwTimeoutMilliseconds(scenario);
            
            this.Logger.AddDebugMessage("Setting timeout for " + scenario + ", " + milliseconds.ToString() + " ms.");

            // The port timeout needs to be considerably longer than the device timeout,
            // otherwise you get "STOPPED" or "NO DATA" somewhat randomly. (I mostly saw
            // this when sending the tool-present messages, but that might be coincidence.)
            //
            // 100 was not enough
            // 150 seems like enough
            // Consider 200 if STOPPED / NO DATA is still a problem. 
            this.Port.SetTimeout(milliseconds + 150);

            // I briefly tried hard-coding timeout values for the AT ST command,
            // but that's a recipe for failure. If the port timeout is shorter
            // than the device timeout, reads will consistently fail.
            int parameter = Math.Min(Math.Max(1, (milliseconds / 4)), 255);
            string value = parameter.ToString("X2");
            await this.SendAndVerify("AT ST " + value, "OK");
        }

        /// <summary>
        /// Send a message, do not expect a response.
        /// </summary>
        public override async Task<bool> SendMessage(Message message)
        {
            byte[] messageBytes = message.GetBytes();
            string header;
            string payload;
            this.ParseMessage(messageBytes, out header, out payload);

            if (header != this.currentHeader)
            {
                string setHeaderResponse = await this.SendRequest("AT SH " + header);
                this.Logger.AddDebugMessage("Set header response: " + setHeaderResponse);

                if(setHeaderResponse == "STOPPED")
                {
                    // Does it help to retry once?
                    setHeaderResponse = await this.SendRequest("AT SH " + header);
                    this.Logger.AddDebugMessage("Set header response: " + setHeaderResponse);
                }

                if (!this.ProcessResponse(setHeaderResponse, "set-header command"))
                {
                    return false;
                }

                this.currentHeader = header;
            }

            payload = payload.Replace(" ", "");

            string sendMessageResponse = await this.SendRequest(payload + " ");

            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (string.IsNullOrWhiteSpace(sendMessageResponse))
                {
                    this.Logger.AddDebugMessage("No response, waiting...");
                    await Task.Delay(100);
                    try
                    {
                        sendMessageResponse = await this.ReadELMLine();
                    }
                    catch (TimeoutException)
                    {
                        this.Logger.AddDebugMessage("Timeout.");
                    }
                }
                else
                {
                    break;
                }
            }

            if (!this.ProcessResponse(sendMessageResponse, "message content"))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Try to read an incoming message from the device.
        /// </summary>
        /// <returns></returns>
        protected override async Task Receive()
        {
            try
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    string response = await this.ReadELMLine();
                    if (response == "STOPPED")
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    this.ProcessResponse(response, "receive");
                    break;
                }

                if (this.ReceivedMessageCount == 0)
                {
                   // await this.ReceiveViaMonitorMode();
                }
            }
            catch (TimeoutException)
            {
                this.Logger.AddDebugMessage("Timeout during receive.");
                // await this.ReceiveViaMonitorMode();
            }
        }

        private async Task ReceiveViaMonitorMode()
        {
            try
            {
                // The code below is currently only supported by Scantool (not AllPro).
                string monitorResponse = await this.SendRequest("AT MA");
                this.Logger.AddDebugMessage("Response to AT MA 1: " + monitorResponse);

                if (monitorResponse != ">?")
                {
                    string response = await this.ReadELMLine();
                    this.ProcessResponse(monitorResponse, "receive via monitor");
                }
            }
            catch(TimeoutException)
            {
                this.Logger.AddDebugMessage("Timeout during receive via monitor mode.");
            }
            finally
            { 
                string stopMonitorResponse = await this.SendRequest("AT MA");
                this.Logger.AddDebugMessage("Response to AT MA 2: " + stopMonitorResponse);
            }
        }

        /// <summary>
        /// Process responses from the EML/ST devices.
        /// </summary>
        private bool ProcessResponse(string rawResponse, string context)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                this.Logger.AddDebugMessage(
                    string.Format("Empty response to {0}.",
                    context));
                return false;
            }

            if (rawResponse == "OK")
            {
                return true;
            }

            string[] segments = rawResponse.Split('<');
            foreach (string segment in segments)
            {
                if (segment.IsHex())
                {
                    string[] hexResponses = segment.Split(' ');
                    foreach (string singleHexResponse in hexResponses)
                    {
                        byte[] deviceResponseBytes = singleHexResponse.ToBytes();
                        if (deviceResponseBytes.Length > 0)
                        {
                            Array.Resize(ref deviceResponseBytes, deviceResponseBytes.Length - 1); // remove checksum byte
                        }

                        this.Logger.AddDebugMessage("RX: " + deviceResponseBytes.ToHex());

                        Message response = new Message(deviceResponseBytes);
                        this.Enqueue(response);
                    }

                    return true;
                }

                if (segment.EndsWith("OK"))
                {
                    this.Logger.AddDebugMessage("WTF: Response not valid, but ends with OK.");
                    return true;
                }

                this.Logger.AddDebugMessage(
                    string.Format(
                        "Unexpected response to {0}: {1}",
                        context,
                        segment));
            }

            return false;
        }
        
        /// <summary>
        /// Separate the message into header and payload.
        /// </summary>
        private void ParseMessage(byte[] messageBytes, out string header, out string payload)
        {
            // The incoming byte array needs to separated into header and payload portions,
            // which are sent separately.
            string hexRequest = messageBytes.ToHex();
            header = hexRequest.Substring(0, 9);
            payload = hexRequest.Substring(9);
        }

        /// <summary>
        /// Send a request in string form, wait for a response (for init)
        /// </summary>
        /// <remarks>
        /// The API for this method (sending a string, returning a string) matches
        /// the way that we need to communicate with ELM and STN devices for setup
        /// </remarks>
        private async Task<string> SendRequest(string request)
        {
            this.Logger.AddDebugMessage("TX: " + request);
            await this.Port.Send(Encoding.ASCII.GetBytes(request + "\r\n"));

            try
            {
                string response = await ReadELMLine();

                return response;
            }
            catch (TimeoutException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Send a command to the device, confirm that we got the response we expect. 
        /// </summary>
        /// <remarks>
        /// This is primarily for use in the Initialize method, where we're talking to the 
        /// interface device rather than the PCM.
        /// </remarks>
        private async Task<bool> SendAndVerify(string message, string expectedResponse)
        {
            string actualResponse = await this.SendRequest(message);

            if (string.Equals(actualResponse, expectedResponse))
            {
                this.Logger.AddDebugMessage(actualResponse);
                return true;
            }

            this.Logger.AddDebugMessage("Did not recieve expected response. " + actualResponse + " does not equal " + expectedResponse);
            return false;
        }

        /// <summary>
        /// Reads and filteres a line from the device, returns it as a string
        /// </summary>
        /// <remarks>
        /// Strips Non Printable, >, and Line Feeds. Converts Carriage Return to Space. Strips leading and trailing whitespace.
        /// </remarks>
        private async Task<string> ReadELMLine()
        {
            int buffersize = (MaxReceiveSize * 3) + 7; // payload with spaces (3 bytes per character) plus the longest possible prompt
            byte[] buffer = new byte[buffersize];

            // collect bytes until we hit the end of the buffer or see a CR or LF
            int i = 0;
            byte[] b = new byte[1]; // FIXME: If I dont copy to this buffer, and instead use buffer[i] inline in the next loop, the test for '>' does not work in the while clause.
            do
            {
                await this.Port.Receive(b, 0, 1);
                //this.Logger.AddDebugMessage("Byte: " + b[0].ToString("X2") + " Ascii: " + System.Text.Encoding.ASCII.GetString(b));
                buffer[i] = b[0];
                i++;
            } while ((i < buffersize) && (b[0] != '>')); // continue until the next prompt

            //this.Logger.AddDebugMessage("Found terminator '>'");

            // count the wanted bytes and replace CR with space
            int wanted = 0;
            int j;
            for (j = 0; j < i; j++)
            {
                if (buffer[j] == 13) buffer[j] = 32; // CR -> Space
                if (buffer[j] >= 32 && buffer[j] <= 126 && buffer[j] != '>') wanted++; // printable characters only, and not '>'
            }

            //this.Logger.AddDebugMessage(wanted + " bytes to keep from " + i);

            // build a message of the correct length
            // i is the length of the data in the original buffer
            // j is pointer to the offset in the filtered buffer
            // k is the pointer in to the original buffer
            int k;
            byte[] filtered = new byte[wanted]; // create a new filtered buffer of the correct size
            for (k = 0, j = 0; k < i; k++) // start both buffers from 0, always increment the original buffer 
            {
                if (buffer[k] >= 32 && buffer[k] <= 126 && buffer[k] != '>') // do we want THIS byte?
                {
                    b[0] = buffer[k];
                    //this.Logger.AddDebugMessage("Filtered Byte: " + buffer[k].ToString("X2") + " Ascii: " + System.Text.Encoding.ASCII.GetString(b));
                    filtered[j++] = buffer[k];  // save it, and increment the pointer in the filtered buffer
                }
            }

            //this.Logger.AddDebugMessage("built filtered string kept " + j + " bytes filtered is " + filtered.Length + " long");
            //this.Logger.AddDebugMessage("filtered: " + filtered.ToHex());
            string line = System.Text.Encoding.ASCII.GetString(filtered).Trim(); // strip leading and trailing whitespace, too

            //this.Logger.AddDebugMessage("Read \"" + line + "\"");

            return line;
        }

        /// <summary>
        /// Collects a line with ReadELMLine() and converts it to a Message
        /// </summary>
        private async Task<Response<Message>> ReadELMPacket()
        {
            //this.Logger.AddDebugMessage("Trace: ReadELMPacket");

            string response = await ReadELMLine();

            byte[] message = response.ToBytes();

            return Response.Create(ResponseStatus.Success, new Message(message));
        }

        /// <summary>
        /// Set the interface to low (false) or high (true) speed
        /// </summary>
        /// <remarks>
        /// The caller must also tell the PCM to switch speeds
        /// </remarks>
        protected override async Task<bool> SetVpwSpeedInternal(VpwSpeed newSpeed)
        {
            if (newSpeed == VpwSpeed.Standard)
            {
                this.Logger.AddDebugMessage("AllPro setting VPW 1X");
                if (!await this.SendAndVerify("AT VPW1", "OK"))
                    return false;
            }
            else
            {
                this.Logger.AddDebugMessage("AllPro setting VPW 4X");
                if (!await this.SendAndVerify("AT VPW4", "OK"))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Discard any messages in the recevied-message queue.
        /// </summary>
        public override void ClearMessageBuffer()
        {
            this.Port.DiscardBuffers();
        }
    }
}