﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Transfer;
using CsvHelper;
using org.ohdsi.cdm.framework.core.Definitions;
using org.ohdsi.cdm.framework.data.DbLayer;
using org.ohdsi.cdm.framework.entities.Omop;
using org.ohdsi.cdm.framework.shared.Enums;
using org.ohdsi.cdm.framework.shared.Helpers;

namespace org.ohdsi.cdm.framework.core.Controllers
{
   public class ChunkController
   {
      private readonly DbKeyOffset dbKeyOffset;
      private readonly DbChunk dbChunk;
      private readonly DbSource dbSource;

      public ChunkController()
      {
         dbKeyOffset = new DbKeyOffset(Settings.Current.Building.BuilderConnectionString);
         dbChunk = new DbChunk(Settings.Current.Building.BuilderConnectionString);
         dbSource = new DbSource(Settings.Current.Building.SourceConnectionString, Path.Combine(new[]
         {
            Settings.Current.Builder.Folder,
            "Common",
            Settings.Current.Building.SourceEngine.Database.ToString()
         }), Settings.Current.Building.SourceSchemaName);
      }


      public void ClenupChunks()
      {
         dbSource.DropChunkTable();
      }

      public void CreateChunks()
      {
          var chunkIds = new List<int>();
          dbKeyOffset.Recreate(Settings.Current.Building.Id.Value);
          dbChunk.ClearChunks(Settings.Current.Building.Id.Value);
          dbSource.CreateChunkTable();
          int i = 0;
          int k = 0;

          using (var saver = Settings.Current.Building.SourceEngine.GetSaver().Create(Settings.Current.Building.SourceConnectionString))
          {
              var chunks = new List<ChunkRecord>();
              foreach (var chunk in GetPersonKeys(Settings.Current.Building.BatchSize))
              {
                  var chunkId = dbChunk.AddChunk(Settings.Current.Building.Id.Value);
                  chunkIds.Add(chunkId);

                  chunks.AddRange(chunk.Select(c => new ChunkRecord { Id = chunkId, PersonId = Convert.ToInt64(c.Key), PersonSource = c.Value }));
                  var chunkSizeOnS3 = 0;
                  if (Settings.Current.Building.BatchSize >= 10000 && Settings.Current.Building.BatchSize < 500000)
                      chunkSizeOnS3 = 200;
                  else if (Settings.Current.Building.BatchSize >= 500000 && Settings.Current.Building.BatchSize < 2000000)
                      chunkSizeOnS3 = 20;
                  else if (Settings.Current.Building.BatchSize >= 2000000)
                      chunkSizeOnS3 = 2;

                  if (Settings.Current.Building.SourceEngine.Database == Database.Redshift &&
                      Settings.Current.Building.BatchSize >= 10000 && i == chunkSizeOnS3)
                  {
                      saver.AddChunk(chunks, k);
                      chunks.Clear();
                      i = 0;
                      k++;
                  }
                  i++;
              }
              if (chunks.Count > 0)
              {
                  saver.AddChunk(chunks, k);
              }

              saver.Commit();
          }
          dbSource.CreateIndexesChunkTable();

         if (Settings.Current.Building.SourceEngine.Database == Database.Redshift)
         {
            MoveChunkDataToS3(chunkIds);
            try
            {
               dbSource.GrantAccessToChunkTable();
            }
            catch (Exception e)
            {
               Logger.Write(null, LogMessageTypes.Warning, "GrantAccessToChunkTable " + Logger.CreateExceptionString(e));
            }
         }
      }

      private static void MoveChunkDataToS3(IEnumerable<int> chunkIds)
      {
         var baseFolder = string.Format("{0}/{1}/{2}/raw", Settings.Current.Bucket, Settings.Current.Building.Vendor, Settings.Current.Building.Id);

         Parallel.ForEach(chunkIds, new ParallelOptions { MaxDegreeOfParallelism = 2 }, cId =>
        {
            var chunkId = cId;
            Parallel.ForEach(Settings.Current.Building.SourceQueryDefinitions, new ParallelOptions { MaxDegreeOfParallelism = 5 }, queryDefinition =>
            {
                try
                {
                    if (queryDefinition.Providers != null) return;
                    if (queryDefinition.Locations != null) return;
                    if (queryDefinition.CareSites != null) return;

                    var sql = queryDefinition.GetSql(Settings.Current.Building.SourceEngine.Database,
                       Settings.Current.Building.Vendor, Settings.Current.Building.SourceSchemaName);

                    if (string.IsNullOrEmpty(sql)) return;

                    sql = string.Format(sql, chunkId);

                    if (queryDefinition.FieldHeaders == null)
                    {
                        StoreMetadataToS3(queryDefinition, sql);
                    }

                    var personIdField = queryDefinition.GetPersonIdFieldName();
                    var tmpTableName = "#" + queryDefinition.FileName + "_" + chunkId;


                    var folder = string.Format("{0}/{1}/{2}", baseFolder, chunkId, queryDefinition.FileName);
                    var fileName = string.Format(@"{0}/{1}", folder, queryDefinition.FileName);

                    var unloadQuery = string.Format(@"create table {0} sortkey ({1}) distkey ({1}) as {2}; " +
                                                    @"UNLOAD ('select * from {0} order by {1}') to 's3://{3}' " +
                                                    @"DELIMITER AS '\t' " + 
                                                    @"credentials 'aws_access_key_id={4};aws_secret_access_key={5}' " +
                                                    @"GZIP ALLOWOVERWRITE PARALLEL ON",
                                                    tmpTableName, //0
                                                    personIdField, //1
                                                    sql, //2
                                                    fileName, //3
                                                    Settings.Current.S3AwsAccessKeyId, //4
                                                    Settings.Current.S3AwsSecretAccessKey); //5

                    using (var connection = SqlConnectionHelper.OpenOdbcConnection(Settings.Current.Building.SourceConnectionString))
                    using (var c = new OdbcCommand(unloadQuery, connection))
                    {
                        c.CommandTimeout = 999999999;
                        c.ExecuteNonQuery();
                    }
                }
                catch (Exception e)
                {
                    Logger.WriteError(chunkId, e);
                    throw;
                }
            });
        });

      }

      private static void StoreMetadataToS3(QueryDefinition queryDefinition, string query)
      {
         var sql = query + " limit 1";
         var metadataKey = string.Format("{0}/{1}/raw/metadata/{2}", Settings.Current.Building.Vendor,
            Settings.Current.Building.Id, queryDefinition.FileName + ".txt");

         using (var conn = SqlConnectionHelper.OpenOdbcConnection(Settings.Current.Building.SourceConnectionString))
         using (var c = Settings.Current.Building.SourceEngine.GetCommand(sql, conn))
         using (var reader = c.ExecuteReader(CommandBehavior.SchemaOnly))
         {
            using (var source = new MemoryStream())
            using (TextWriter writer = new StreamWriter(source, new UTF8Encoding(false, true)))
            using (var csv = new CsvWriter(writer, new CsvHelper.Configuration.Configuration
            {
               HasHeaderRecord = false,
               Delimiter = ",",
               Encoding = Encoding.UTF8
            }))
            {
               for (var i = 0; i < reader.FieldCount; i++)
               {
                  csv.WriteField(reader.GetName(i));
               }
               csv.NextRecord();
               writer.Flush();
               
               using (var client = new AmazonS3Client(Settings.Current.S3AwsAccessKeyId, Settings.Current.S3AwsSecretAccessKey, Amazon.RegionEndpoint.USEast1))
               using (var directoryTransferUtility = new TransferUtility(client))
               {
                  directoryTransferUtility.Upload(new TransferUtilityUploadRequest
                  {
                     BucketName = Settings.Current.Bucket,
                     Key = metadataKey,
                     ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
                     StorageClass = S3StorageClass.ReducedRedundancy,
                     InputStream = source
                  });
               }
            }
         }
      }

      public IEnumerable<List<KeyValuePair<string, string>>> GetPersonKeys(int batchSize)
      {
         return GetPersonKeys(Settings.Current.Building.Batches, batchSize);
      }

      public bool AllChunksStarted()
      {
         return dbChunk.AllChunksStarted(Settings.Current.Building.Id.Value);
      }

      public bool AllChunksComplete()
      {
         return dbChunk.AllChunksComplete(Settings.Current.Building.Id.Value);
      }

      public int GetChunksCount()
      {
         return dbChunk.GetChunksCount(Settings.Current.Building.Id.Value);
      }

      public int GetCompleteChunksCount()
      {
         return dbChunk.GetCompleteChunksCount(Settings.Current.Building.Id.Value);
      }

      public IEnumerable<List<KeyValuePair<string, string>>> GetPersonKeys(long batches, int batchSize)
      {
         var batch = new List<KeyValuePair<string, string>>(batchSize);

         var query = GetSqlHelper.GetSql(Settings.Current.Building.SourceEngine.Database, Settings.Current.Building.BatchScript, Settings.Current.Building.SourceSchemaName);
         
         foreach (var reader in dbSource.GetPersonKeys(query, batches, batchSize))
         {
            if (batch.Count == batchSize)
            {
               yield return batch;
               batch.Clear();
            }

            var id = reader[0].ToString().Trim();
            var source = reader[1].ToString().Trim(); // ? replace to reader[1].ToString(), without Trim()

            batch.Add(new KeyValuePair<string, string>(id, source));
         }

         yield return batch;
      }

      public void ResetNotFinishedChunks()
      {
         dbChunk.ResetNotFinishedChunks(Settings.Current.Building.Id.Value);
      }

      public void ResetChunks()
      {
         dbChunk.ResetChunks(Settings.Current.Building.Id.Value);
         dbKeyOffset.Recreate(Settings.Current.Building.Id.Value);
      }
   }
}
