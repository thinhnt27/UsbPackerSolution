using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Data.Sqlite;

namespace UsbPacker
{
    public static class LocalDatabase
    {
        // ✅ Lấy đường dẫn local.db cùng cấp với thư mục Done
        private static string GetDbPath()
        {
            string baseFolder = AppDomain.CurrentDomain.BaseDirectory;
            string dbPath = Path.Combine(baseFolder, "local.db");
            return dbPath;
        }

        private static void EnsureTable()
        {
            var dbPath = GetDbPath();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS hash_info (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    hash_value TEXT NOT NULL,
                    subject_name TEXT,
                    timestamp TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        public static void InsertHash(string hash, string subjectName)
        {
            EnsureTable();
            var dbPath = GetDbPath();
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO hash_info (hash_value, subject_name, timestamp)
                VALUES ($hash, $subject, $time);
            ";
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$subject", subjectName ?? "(unknown)");
            cmd.Parameters.AddWithValue("$time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
    }
}
