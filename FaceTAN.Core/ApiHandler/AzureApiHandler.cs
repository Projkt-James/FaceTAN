﻿using FaceTAN.Core.Data;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;
using Polly;
using FaceTAN.Core.Data.Models.Timing;
using System.Diagnostics;
using System.Web.Script.Serialization;

namespace FaceTAN.Core.ApiHandler
{
    public class AzureApiHandler : BaseApiHandler
    {
        public AzureApiHandler(ApiKeyStore apiKeys, string region, string config, string personGroupId, DataSet dataSet)
        {
            ApiName = "Azure";
            ApiKeys = apiKeys;
            Region = region;
            Client = new FaceServiceClient(ApiKeys.GetCurrentKey(), Region);
            DataSet = dataSet;
            PersonGroupId = personGroupId;
            TargetFaceList = new List<Face>();
            SourceFaceList = new List<Face>();
            SourceMatchList = new List<IdentifyResult>();
            TimingResults = new List<TimingModel>();
        }

        private ApiKeyStore ApiKeys;

        private FaceServiceClient Client { get; set; }

        private DataSet DataSet { get; }

        private string Region { get; }

        private string PersonGroupId { get; }

        private List<Face> TargetFaceList { get; set; }

        private List<Face> SourceFaceList { get; set; }

        private List<IdentifyResult> SourceMatchList { get; set; }

        private List<TimingModel> TimingResults { get; set; }

        public override async Task RunApi()
        {
            await InitApiAsync();
            await MatchSourceFaces();
            return;
        }

        public override ApiResults ReturnJsonResults()
        {
            var indexedString = new JavaScriptSerializer().Serialize((TargetFaceList.Concat(SourceFaceList)));
            var matchedString = new JavaScriptSerializer().Serialize(SourceMatchList);
            var timingString = new JavaScriptSerializer().Serialize(TimingResults);

            return new ApiResults(indexedString, matchedString, timingString);
        }

        public override void ExportResults(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory + "\\Azure");

            JsonSerializer serializer = new JsonSerializer();
            using (StreamWriter file = File.CreateText(outputDirectory + "\\Azure\\Azure_Target_Face_Data.txt"))
            {
                serializer.Serialize(file, TargetFaceList);
                Console.WriteLine("Wrote azure target face data to {0}.", outputDirectory + "\\Azure\\Azure_Target_Face_Data.txt");
            }
            using (StreamWriter file = File.CreateText(outputDirectory + "\\Azure\\Azure_Source_Face_Data.txt"))
            {
                serializer.Serialize(file, SourceFaceList);
                Console.WriteLine("Wrote azure source face data to {0}.", outputDirectory + "\\Azure\\Azure_Source_Face_Data.txt");
            }
            using (StreamWriter file = File.CreateText(outputDirectory + "\\Azure\\Azure_Match_Data.txt"))
            {
                serializer.Serialize(file, SourceMatchList);
                Console.WriteLine("Wrote azure face match data to {0}.", outputDirectory + "\\Azure\\Azure_Match_Data.txt");
            }
            using (StreamWriter file = File.CreateText(outputDirectory + "\\Azure\\Azure_Timing_Results.txt"))
            {
                serializer.Serialize(file, TimingResults);
                Console.WriteLine("Wrote azure timing results to {0}.", outputDirectory + "\\Azure\\Azure_Timing_Results.txt");
            }
        }

        private async Task InitApiAsync()
        {
            PersonGroup[] existingGroups = await Client.ListPersonGroupsAsync();
            if (existingGroups.ToList().Find(p => p.PersonGroupId == PersonGroupId) != null)
            {
                Console.WriteLine("Existing person group found with id {0}. Deleting...", PersonGroupId);
                await Client.DeletePersonGroupAsync(PersonGroupId);
            }

            Console.WriteLine("Creating person group: {0}.", PersonGroupId);
            await Client.CreatePersonGroupAsync(PersonGroupId, "Test Group");

            foreach (var entry in DataSet.TargetImages)
            {
                await AddTarget(entry);
            }
        }

        private async Task AddTarget(KeyValuePair<string, Image> entry)
        {
            Guid personId = new Guid();
            Face personFace = null;
            
            var addRetryPolicy = Policy.Handle<FaceAPIException>().WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(16), (ex, timeSpan) =>
            {
                Console.WriteLine("API key {0} failed. Changing key.", ApiKeys.GetCurrentKey());
                ApiKeys.NextKey();
                Client = new FaceServiceClient(ApiKeys.GetCurrentKey(), Region);
                Console.WriteLine("Now using API key {0}.", ApiKeys.GetCurrentKey());

            });
            personId = await addRetryPolicy.ExecuteAsync(async () => await AddPerson(entry.Key));

            if (personId == new Guid())
            {
                Console.WriteLine("Failed to create person {0}.", entry.Key);
                return;
            }

            var findRetryPolicy = Policy.Handle<FaceAPIException>().WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(16), (ex, timeSpan) =>
            {
                Console.WriteLine("API key {0} failed. Changing key.", ApiKeys.GetCurrentKey());
                ApiKeys.NextKey();
                Client = new FaceServiceClient(ApiKeys.GetCurrentKey(), Region);
                Console.WriteLine("Now using API key {0}.", ApiKeys.GetCurrentKey());
            });
            personFace = await findRetryPolicy.ExecuteAsync(async () => await FindFace(entry.Key));

            if (personFace == null)
            {
                Console.WriteLine("Failed to find face in image.");
                return;
            }

            var addFaceRetryPolicy = Policy.Handle<FaceAPIException>().WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(16), (ex, timeSpan) =>
            {
                Console.WriteLine("API key {0} failed. Changing key.", ApiKeys.GetCurrentKey());
                ApiKeys.NextKey();
                Client = new FaceServiceClient(ApiKeys.GetCurrentKey(), Region);
                Console.WriteLine("Now using API key {0}.", ApiKeys.GetCurrentKey());
            });
            await addFaceRetryPolicy.ExecuteAsync(async () => await AddFaceToPerson(entry.Key, personId, personFace));


            var trainRetryPolicy = Policy.Handle<FaceAPIException>().WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(16), (ex, timeSpan) =>
            {
                Console.WriteLine("API key {0} failed. Changing key.", ApiKeys.GetCurrentKey());
                ApiKeys.NextKey();
                Client = new FaceServiceClient(ApiKeys.GetCurrentKey(), Region);
                Console.WriteLine("Now using API key {0}.", ApiKeys.GetCurrentKey());
            });
            await trainRetryPolicy.ExecuteAsync(async () => await TrainPersonGroup());
        }

        private async Task<Guid> AddPerson(string key)
        {
            Console.WriteLine("Attempting to creating person: {0}.", key);
            var watch = Stopwatch.StartNew();
            CreatePersonResult person = await Client.CreatePersonAsync(PersonGroupId, key);
            watch.Stop();
            TimingResults.Add(new TimingModel("AddPerson", key, watch.ElapsedMilliseconds));
            return person.PersonId;
        }

        private async Task<Face> FindFace(string key)
        {
            Console.WriteLine("Attempting to locate face in image.");
            Face[] faces = await Client.DetectAsync(DataSet.GetImageStream(key));


            if (faces.Length == 0)
                return null;
            else
            {
                TargetFaceList.AddRange(faces);
                return faces[0];
            }
        }

        private async Task AddFaceToPerson(string key, Guid personId, Face personFace)
        {
            Console.WriteLine("Adding face to person {0}.", key);
            var watch = Stopwatch.StartNew();
            await Client.AddPersonFaceAsync(PersonGroupId, personId, DataSet.GetImageStream(key), null, personFace.FaceRectangle);
            watch.Stop();
            TimingResults.Add(new TimingModel("AddFaceToPerson", key, watch.ElapsedMilliseconds));
        }

        private async Task TrainPersonGroup()
        {
            Console.WriteLine("Training PersonGroup {0} after adding new face.", PersonGroupId);
            var watch = Stopwatch.StartNew();
            await Client.TrainPersonGroupAsync(PersonGroupId);
            watch.Stop();
            TimingResults.Add(new TimingModel("TrainPersonGroup", "", watch.ElapsedMilliseconds));
        }

        private async Task MatchSourceFaces()
        {
            foreach(var entry in DataSet.SourceImages)
            {
                var matchRetryPolicy = Policy.Handle<FaceAPIException>().WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(16), (ex, timeSpan) =>
                {
                    Console.WriteLine("API key {0} failed. Changing key.", ApiKeys.GetCurrentKey());
                    ApiKeys.NextKey();
                    Client = new FaceServiceClient(ApiKeys.GetCurrentKey(), Region);
                    Console.WriteLine("Now using API key {0}.", ApiKeys.GetCurrentKey());
                });
                await matchRetryPolicy.ExecuteAsync(async () => await MatchSourceFace(entry));
            }
        }

        private async Task MatchSourceFace(KeyValuePair<string, Image> entry)
        {
            Console.WriteLine("Attempting to match face of person {0}.", entry.Key);

            var watch = Stopwatch.StartNew();
            Face[] faces = await Client.DetectAsync(DataSet.GetImageStream(entry.Key));
            Guid[] faceIds = faces.Select(face => face.FaceId).ToArray();
            IdentifyResult[] results = await Client.IdentifyAsync(PersonGroupId, faceIds);
            watch.Stop();
            TimingResults.Add(new TimingModel("Identify Source Face", entry.Key, watch.ElapsedMilliseconds));

            SourceFaceList.AddRange(faces);
            SourceMatchList.AddRange(results);

            foreach (var identifyResult in results)
            {
                if (identifyResult.Candidates.Length == 0)
                    Console.WriteLine("Unable to find match.");
                else
                {
                    var candidateId = identifyResult.Candidates[0].PersonId;
                    Console.WriteLine("Face identified as {0}", entry.Key);
                }
            }
        }
    }
}
