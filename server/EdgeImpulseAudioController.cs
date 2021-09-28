using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;

/// <summary>
/// This web api provides methods for accepting a POST that contains a .wav audio file
/// </summary>
public class EdgeImpulseAudioController : ApiController
{
    // #########
    // constants
    // #########
    readonly string LOCAL_DIR = @"<path to working directory on server>";
    readonly string EI_API_KEY = "<your edge impulse api key>";
    // please note the following should be passed into the PostAudioFile method instead of hardcoded here!
    readonly string EI_LABEL = "<label of audio file>";
    readonly string EI_FILENAME = "<file name of audio file>";
    readonly string EI_DEVICE_NAME = "<device name from ei studio>";
    readonly string EI_DEVICE_TYPE = "<device type from ei studio>";

    [HttpPost, ActionName("PostAudioFile")]
    [Route("")]
    public async Task<string> PostAudioFile(string guid, int note_number, int note_index, decimal samplerate)
    {
        // ################################################################################
        // step 1: read binary data from the POST, convert it to base64, save to local file
        // ################################################################################

        try
        {
            byte[] form_data = await Request.Content.ReadAsByteArrayAsync();
            string encoded_form_data = Convert.ToBase64String(form_data);
            File.WriteAllText(LOCAL_DIR + @"\" + note_index + " -" + guid + ".txt", encoded_form_data, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject("{\"err\":\"" + ex.ToString() + "\"}");
        }


        // ###########################################################################################
        // step 2: after a file is written, check filesystem to see if we have all the chunks expected
        // ###########################################################################################

        try
        {
            DirectoryInfo d = new DirectoryInfo(LOCAL_DIR);
            FileInfo[] Files = d.GetFiles("*.txt");
            int file_count = 0;

            foreach (FileInfo file in Files)
            {
                if (file.Name.Contains(guid))
                    file_count++;
            }

            if (file_count >= note_number)
            {
                // #############################################################
                // step 3: if all chunks accounted for, reassemble the .wav file
                // #############################################################

                StringBuilder sb = new StringBuilder();

                for (int i = 1; i <= note_number; i++)
                {
                    string content = File.ReadAllText(LOCAL_DIR + @"\" + i + " -" + guid + ".txt", Encoding.UTF8);
                    sb.Append(content);
                }

                byte[] wav_data = Convert.FromBase64String(sb.ToString());
                File.WriteAllBytes(LOCAL_DIR + @"\" + guid + ".wav", wav_data);


                // ############################################################
                // step 4: send the .wav file to the edge impulse ingestion api
                // ############################################################

                var httpWebRequest = (HttpWebRequest)WebRequest.Create("https://ingestion.edgeimpulse.com/api/training/data");
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = "POST";
                httpWebRequest.Headers.Add("x-file-name", EI_FILENAME);
                httpWebRequest.Headers.Add("x-label", EI_LABEL);
                httpWebRequest.Headers.Add("x-api-key", EI_API_KEY);

                TimeSpan span = DateTime.Now.Subtract(new DateTime(1970, 1, 1, 0, 0, 0));

                double[] left;
                double[] right;

                // extract *only* the wav file data (WITHOUT the header!)
                ExtractWavData(LOCAL_DIR + @"\" + guid + ".wav", out left, out right);

                using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
                {
                    string json = "{" +
                                  "\"protected\":" +
                                  "    {" +
                                  "        \"ver\": \"v1\"," +
                                  "        \"alg\": \"none\"," +
                                  "        \"iat\": " + span.TotalSeconds +
                                  "    }," +
                                  "\"payload\":" +
                                  "    {" +
                                  "        \"device_name\": \"" + EI_DEVICE_NAME + "\"," +
                                  "        \"device_type\": \"" + EI_DEVICE_TYPE + "\"," +
                                  "        \"interval_ms\": " + samplerate + "," +
                                  "        \"sensors\": [{\"name\": \"audio\", \"units\": \"wav\"}]," +
                                  "        \"values\": [" + string.Join(",", left) + "]" +
                                  "    }" +
                                  "}";

                    streamWriter.Write(json);
                }

                var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
                var responseString = new StreamReader(httpResponse.GetResponseStream()).ReadToEnd();
            }

        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject("{\"err\":\"" + ex.ToString() + "\"}");
        }

        return JsonConvert.SerializeObject(200);
    }

    public static double BytesToDouble(byte firstByte, byte secondByte)
    {
        short s = (short)((secondByte << 8) | firstByte);
        return s;
    }

    public void ExtractWavData(string filename, out double[] left, out double[] right)
    {
        // Returns left and right double arrays. 'right' will be null if sound is mono.
        byte[] wav = File.ReadAllBytes(filename);

        // Determine if mono or stereo
        int channels = wav[22];     // Forget byte 23 as 99.999% of WAVs are 1 or 2 channels

        // Get past all the other sub chunks to get to the data subchunk:
        int pos = 12;   // First Subchunk ID from 12 to 16

        // Keep iterating until we find the data chunk (i.e. 64 61 74 61 ...... (i.e. 100 97 116 97 in decimal))
        while (!(wav[pos] == 100 && wav[pos + 1] == 97 && wav[pos + 2] == 116 && wav[pos + 3] == 97))
        {
            pos += 4;
            int chunkSize = wav[pos] + wav[pos + 1] * 256 + wav[pos + 2] * 65536 + wav[pos + 3] * 16777216;
            pos += 4 + chunkSize;
        }
        pos += 8;

        // Pos is now positioned to start of actual sound data.
        int samples = (wav.Length - pos) / 2;     // 2 bytes per sample (16 bit sound mono)
        if (channels == 2) samples /= 2;        // 4 bytes per sample (16 bit stereo)

        // Allocate memory (right will be null if only mono sound)
        left = new double[samples];
        if (channels == 2) right = new double[samples];
        else right = null;

        // Write to double array/s:
        int i = 0;
        while (pos < wav.Length)
        {
            left[i] = BytesToDouble(wav[pos], wav[pos + 1]);
            pos += 2;
            if (channels == 2)
            {
                right[i] = BytesToDouble(wav[pos], wav[pos + 1]);
                pos += 2;
            }
            i++;
        }
    }

    [HttpGet, ActionName("GetTest")]
    public string GetTest()
    {
        return "Hello World!";
    }
}