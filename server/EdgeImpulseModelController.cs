using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Web.Http;

/// <summary>
/// This web api provides methods for chunking a file and sending it back to a notecard
/// </summary>
public class EdgeImpulseModelController : ApiController
{
    // #########
    // constants
    // #########
    readonly double CHUNK_SIZE = 8000;
    readonly string LOCAL_DIR = @"<path to working directory on server>";
    readonly string EI_API_KEY = "<your edge impulse api key>";
    readonly string NOTEHUB_PRODUCT_UID = "<your notehub product uid>";
    readonly string NOTECARD_DEVICE_UID = "<your notecard device id>";
    readonly string NOTEHUB_SESSION_TOKEN = "<your notehub api token>";

    [HttpGet, ActionName("BuildModelFile")]
    public string BuildModelFile(int ei_project_id)
    {
        // #######################################
        // step 1: build the model in edge impulse
        // #######################################

        var reqBuild = (HttpWebRequest)WebRequest.Create("https://studio.edgeimpulse.com/v1/api/" + ei_project_id + "/jobs/build-ondevice-model?type=zip");
        reqBuild.Accept = "application/json";
        reqBuild.ContentType = "application/json";
        reqBuild.Method = "POST";
        reqBuild.Headers.Add("x-api-key", EI_API_KEY);

        using (var streamWriter = new StreamWriter(reqBuild.GetRequestStream()))
        {
            string json = "{" +
                          "    \"engine\": \"tflite\"," +
                          "    \"modelType\": \"int8\"" +
                          "}";

            streamWriter.Write(json);
        }

        var rspBuild = (HttpWebResponse)reqBuild.GetResponse();
        var rspBuildString = new StreamReader(rspBuild.GetResponseStream()).ReadToEnd();
        var joBuild = JObject.Parse(rspBuildString);

        if (!(bool)joBuild["success"] || joBuild["id"] == null)
            return JsonConvert.SerializeObject(joBuild);

        int job_id = (int)joBuild["id"];


        // #########################################################
        // step 2: check the status of the build job in edge impulse
        // #########################################################

        bool job_complete = false;

        while (!job_complete)
        {
            var reqStatus = (HttpWebRequest)WebRequest.Create("https://studio.edgeimpulse.com/v1/api/" + ei_project_id + "/jobs/" + job_id + "/status");
            reqStatus.Accept = "application/json";
            reqStatus.Method = "GET";
            reqStatus.Headers.Add("x-api-key", EI_API_KEY);

            var rspStatus = (HttpWebResponse)reqStatus.GetResponse();
            var rspStatusString = new StreamReader(rspStatus.GetResponseStream()).ReadToEnd();
            var joStatus = JObject.Parse(rspStatusString);

            if (joStatus["success"] != null && joStatus["job"]["finishedSuccessful"] != null &&
                (bool)joStatus["success"] && (bool)joStatus["job"]["finishedSuccessful"])
            {
                job_complete = true;
            }
            else
            {
                System.Threading.Thread.Sleep(5000);
            }
        }


        // ##################################################
        // step 3: download, zip, and split up the model file
        // ##################################################

        // TODO: figure out the type
        var reqDownload = (HttpWebRequest)WebRequest.Create("https://studio.edgeimpulse.com/v1/api/" + ei_project_id + "/deployment/download?type=zip&modelType=int8");
        reqDownload.Accept = "application/zip";
        reqDownload.Method = "GET";
        reqDownload.Headers.Add("x-api-key", EI_API_KEY);

        var rspDownload = (HttpWebResponse)reqDownload.GetResponse();

        // delete existing resources if previously downloaded/extracted
        if (File.Exists(LOCAL_DIR + "modelfile.zip"))
            File.Delete(LOCAL_DIR + "modelfile.zip");

        var dir = new DirectoryInfo(LOCAL_DIR + "modelfile");
        if (dir.Exists)
            dir.Delete(true);

        if (File.Exists(LOCAL_DIR + "modelfile_optimized.zip"))
            File.Delete(LOCAL_DIR + "modelfile_optimized.zip");

        if (File.Exists(LOCAL_DIR + "modelfile_optimized.txt"))
            File.Delete(LOCAL_DIR + "modelfile_optimized.txt");
        // done deleting resources

        // write the stream to the "modelfile.zip" file
        using (Stream output = File.OpenWrite(LOCAL_DIR + "modelfile.zip"))
        using (Stream input = rspDownload.GetResponseStream())
            input.CopyTo(output);

        // unzip the file
        ZipFile.ExtractToDirectory(LOCAL_DIR + "modelfile.zip", LOCAL_DIR + "modelfile");

        // delete "edge-impulse-sdk" and "CMakeLists.txt" (because they're already on the pi!)
        dir = new DirectoryInfo(LOCAL_DIR + "modelfile\\edge-impulse-sdk");
        if (dir.Exists)
            dir.Delete(true);

        if (File.Exists(LOCAL_DIR + "modelfile\\CMakeLists.txt"))
            File.Delete(LOCAL_DIR + "modelfile\\CMakeLists.txt");

        // zip up this trimmed directory
        ZipFile.CreateFromDirectory(LOCAL_DIR + "modelfile", LOCAL_DIR + "modelfile_optimized.zip", CompressionLevel.Optimal, false);

        // convert it to a base64-encoded string and save to text file
        byte[] zip_bytes = File.ReadAllBytes(LOCAL_DIR + "modelfile_optimized.zip");
        string zip_file_base64 = Convert.ToBase64String(zip_bytes);
        File.WriteAllText(LOCAL_DIR + "modelfile_optimized.txt", zip_file_base64);

        // determine the number of file "chunks" we need to split this into
        double file_bytes = new FileInfo(LOCAL_DIR + "modelfile_optimized.txt").Length;
        decimal total_chunks = Math.Ceiling((decimal)(file_bytes / CHUNK_SIZE));

        // split up the file
        ChunkFile(LOCAL_DIR, "modelfile_optimized.txt");



        // ########################################################################################
        // step 4: send an inbound note to the notecard to notify that there is a model file update
        // ########################################################################################

        var reqNote = (HttpWebRequest)WebRequest.Create("https://api.notefile.net/req?product=" + NOTEHUB_PRODUCT_UID + "&device=" + NOTECARD_DEVICE_UID);
        reqNote.Accept = "application/json";
        reqNote.ContentType = "application/json";
        reqNote.Method = "POST";
        reqNote.Headers.Add("x-session-token", NOTEHUB_SESSION_TOKEN);

        using (var streamWriter = new StreamWriter(reqNote.GetRequestStream()))
        {
            string json = "{" +
                          "    \"req\": \"note.add\"," +
                          "    \"file\": \"model-update.qi\"," +
                          "    \"body\": " +
                          "    { " +
                          "        \"filename\": \"modelfile_optimized.txt\"," +
                          "        \"total_chunks\": " + total_chunks +
                          "    } " +
                          "}";

            streamWriter.Write(json);
        }

        var rspNote = (HttpWebResponse)reqNote.GetResponse();

        //return JsonConvert.SerializeObject(rspNote.GetResponseStream());

        return JsonConvert.SerializeObject("200");
    }

    public class FileChunk
    {
        public string payload { get; set; }
        public string md5_checksum { get; set; }
    }

    [HttpGet, ActionName("GetModelFileChunk")]
    public FileChunk GetModelFileChunk(int chunk, string filename)
    {
        // gets a single file chunk as requested
        string file_text = File.ReadAllText(LOCAL_DIR + chunk + "-" + filename);

        FileChunk f = new FileChunk();
        f.payload = file_text;
        f.md5_checksum = CalculateMD5(LOCAL_DIR + filename);
        return f;
    }

    public void ChunkFile(string path, string filename)
    {
        StreamReader rdr = File.OpenText(path + filename);
        string fileBuffer = rdr.ReadToEnd();
        rdr.Close();

        int fileCounter = 0;
        while (fileBuffer.Length > 0)
        {
            fileCounter++;
            string fileChunkBuffer;
            if (fileBuffer.Length > CHUNK_SIZE)
            {
                fileChunkBuffer = fileBuffer.Substring(0, (int)CHUNK_SIZE);
                fileBuffer = fileBuffer.Substring((int)CHUNK_SIZE);
            }
            else
            {
                fileChunkBuffer = fileBuffer;
                fileBuffer = "";
            }

            // write this chunk file
            string chunkFileName = path + fileCounter + "-" + filename;
            if (File.Exists(chunkFileName))
                File.Delete(chunkFileName);
            StreamWriter wtr = File.CreateText(chunkFileName);
            wtr.Write(fileChunkBuffer);
            wtr.Close();
        }
    }

    public string CalculateMD5(string filename)
    {
        using (var md5 = MD5.Create())
        {
            using (var stream = File.OpenRead(filename))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    [HttpGet, ActionName("GetTest")]
    public string GetTest()
    {
        return "Hello World!";
    }
}