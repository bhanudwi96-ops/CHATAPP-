using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using BCrypt.Net;

namespace ChatApp.DbSeeder
{
    public class UserSeedModel
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string ProfilePictureUrl { get; set; } = string.Empty;
        public int Status { get; set; } // 0 = Online, 1 = Away, 2 = Busy, 3 = Offline
        public string StatusName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeen { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    internal class Program
    {
        private static readonly string ConnectionString = 
            "Server=(localdb)\\mssqllocaldb;Database=ChatAppDb;Trusted_Connection=true;MultipleActiveResultSets=true;TrustServerCertificate=True";

        static async Task Main(string[] args)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("ChatApp - 100 Dummy Users Generator & DB Updater");
            Console.WriteLine("=================================================");

            var rawProfiles = Get100UserProfiles();
            var users = new List<UserSeedModel>();

            Console.WriteLine("Generating BCrypt password hashes and full user records...");
            // Standard test password for dummy users
            const string defaultPassword = "Password123!";
            string commonPasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword, 10);

            var random = new Random(42); // Deterministic seed for repeatable realistic timestamps

            for (int i = 0; i < rawProfiles.Count; i++)
            {
                var profile = rawProfiles[i];
                var id = Guid.NewGuid();
                int statusVal = i % 4; // Distribute across Online (0), Away (1), Busy (2), Offline (3)
                string statusName = statusVal switch
                {
                    0 => "Online",
                    1 => "Away",
                    2 => "Busy",
                    _ => "Offline"
                };

                int daysAgo = random.Next(1, 120);
                int minutesAgo = random.Next(2, 2880);
                var createdAt = DateTime.UtcNow.AddDays(-daysAgo).AddMinutes(random.Next(0, 1440));
                DateTime? lastSeen = statusVal == 0 ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-minutesAgo);

                // Realistic avatar URL with diverse styles and unique seed
                string avatarUrl = $"https://api.dicebear.com/7.x/avataaars/svg?seed={profile.Username}&backgroundColor=b6e3f4,c0aede,d1d4f9,ffd5dc,ffdfbf";

                users.Add(new UserSeedModel
                {
                    Id = id,
                    Username = profile.Username,
                    Email = profile.Email,
                    DisplayName = profile.DisplayName,
                    Password = defaultPassword,
                    PasswordHash = commonPasswordHash,
                    ProfilePictureUrl = avatarUrl,
                    Status = statusVal,
                    StatusName = statusName,
                    CreatedAt = createdAt,
                    LastSeen = lastSeen,
                    UpdatedAt = null
                });
            }

            Console.WriteLine($"Generated {users.Count} dummy users.");

            // Update Database
            Console.WriteLine("\nConnecting to database and updating Users table...");
            using (var connection = new SqlConnection(ConnectionString))
            {
                await connection.OpenAsync();
                Console.WriteLine("Database connection successfully established.");

                // Check existing users
                var existingUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var existingEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var cmd = new SqlCommand("SELECT Username, Email FROM Users", connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        existingUsernames.Add(reader.GetString(0));
                        existingEmails.Add(reader.GetString(1));
                    }
                }

                Console.WriteLine($"Found {existingUsernames.Count} existing users in database.");

                int insertedCount = 0;
                int skippedCount = 0;

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var user in users)
                        {
                            if (existingUsernames.Contains(user.Username) || existingEmails.Contains(user.Email))
                            {
                                skippedCount++;
                                continue;
                            }

                            string insertSql = @"
                                INSERT INTO Users (Id, Username, Email, PasswordHash, DisplayName, ProfilePictureUrl, Status, LastSeen, CreatedAt, UpdatedAt)
                                VALUES (@Id, @Username, @Email, @PasswordHash, @DisplayName, @ProfilePictureUrl, @Status, @LastSeen, @CreatedAt, @UpdatedAt);
                            ";

                            using (var insertCmd = new SqlCommand(insertSql, connection, transaction))
                            {
                                insertCmd.Parameters.AddWithValue("@Id", user.Id);
                                insertCmd.Parameters.AddWithValue("@Username", user.Username);
                                insertCmd.Parameters.AddWithValue("@Email", user.Email);
                                insertCmd.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);
                                insertCmd.Parameters.AddWithValue("@DisplayName", user.DisplayName);
                                insertCmd.Parameters.AddWithValue("@ProfilePictureUrl", (object?)user.ProfilePictureUrl ?? DBNull.Value);
                                insertCmd.Parameters.AddWithValue("@Status", user.Status);
                                insertCmd.Parameters.AddWithValue("@LastSeen", (object?)user.LastSeen ?? DBNull.Value);
                                insertCmd.Parameters.AddWithValue("@CreatedAt", user.CreatedAt);
                                insertCmd.Parameters.AddWithValue("@UpdatedAt", (object?)user.UpdatedAt ?? DBNull.Value);

                                await insertCmd.ExecuteNonQueryAsync();
                                insertedCount++;
                            }
                        }

                        transaction.Commit();
                        Console.WriteLine($"Successfully inserted {insertedCount} users into ChatAppDb! (Skipped duplicates: {skippedCount})");
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        Console.WriteLine($"Error inserting users: {ex.Message}");
                        throw;
                    }
                }

                // Verify final count in DB
                using (var countCmd = new SqlCommand("SELECT COUNT(*) FROM Users", connection))
                {
                    int totalInDb = (int)(await countCmd.ExecuteScalarAsync() ?? 0);
                    Console.WriteLine($"Total users in ChatAppDb now: {totalInDb}");
                }
            }

            // Save JSON artifact
            string jsonOutput = JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true });
            string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dummy_users_100.json");
            await File.WriteAllTextAsync(outputPath, jsonOutput);
            Console.WriteLine($"Saved dummy users JSON to: {outputPath}");

            Console.WriteLine("\n[DONE] Completed seeding 100 dummy users!");
        }

        private static List<(string Username, string DisplayName, string Email)> Get100UserProfiles()
        {
            return new List<(string Username, string DisplayName, string Email)>
            {
                ("alex.turner", "Alex Turner", "alex.turner@example.com"),
                ("sophia.martinez", "Sophia Martinez", "sophia.martinez@example.com"),
                ("liam.johnson", "Liam Johnson", "liam.johnson@example.com"),
                ("emma.williams", "Emma Williams", "emma.williams@example.com"),
                ("noah.brown", "Noah Brown", "noah.brown@example.com"),
                ("olivia.davis", "Olivia Davis", "olivia.davis@example.com"),
                ("james.miller", "James Miller", "james.miller@example.com"),
                ("ava.wilson", "Ava Wilson", "ava.wilson@example.com"),
                ("lucas.moore", "Lucas Moore", "lucas.moore@example.com"),
                ("isabella.taylor", "Isabella Taylor", "isabella.taylor@example.com"),
                ("mason.anderson", "Mason Anderson", "mason.anderson@example.com"),
                ("mia.thomas", "Mia Thomas", "mia.thomas@example.com"),
                ("ethan.jackson", "Ethan Jackson", "ethan.jackson@example.com"),
                ("harper.white", "Harper White", "harper.white@example.com"),
                ("oliver.harris", "Oliver Harris", "oliver.harris@example.com"),
                ("evelyn.martin", "Evelyn Martin", "evelyn.martin@example.com"),
                ("elijah.thompson", "Elijah Thompson", "elijah.thompson@example.com"),
                ("charlotte.garcia", "Charlotte Garcia", "charlotte.garcia@example.com"),
                ("benjamin.martinez", "Benjamin Martinez", "benjamin.martinez@example.com"),
                ("amelia.robinson", "Amelia Robinson", "amelia.robinson@example.com"),
                ("lucas.clark", "Lucas Clark", "lucas.clark@example.com"),
                ("abigail.rodriguez", "Abigail Rodriguez", "abigail.rodriguez@example.com"),
                ("henry.lewis", "Henry Lewis", "henry.lewis@example.com"),
                ("emily.lee", "Emily Lee", "emily.lee@example.com"),
                ("alexander.walker", "Alexander Walker", "alexander.walker@example.com"),
                ("elizabeth.hall", "Elizabeth Hall", "elizabeth.hall@example.com"),
                ("sebastian.allen", "Sebastian Allen", "sebastian.allen@example.com"),
                ("sofia.young", "Sofia Young", "sofia.young@example.com"),
                ("daniel.hernandez", "Daniel Hernandez", "daniel.hernandez@example.com"),
                ("avery.king", "Avery King", "avery.king@example.com"),
                ("matthew.wright", "Matthew Wright", "matthew.wright@example.com"),
                ("ella.lopez", "Ella Lopez", "ella.lopez@example.com"),
                ("samuel.hill", "Samuel Hill", "samuel.hill@example.com"),
                ("scarlett.scott", "Scarlett Scott", "scarlett.scott@example.com"),
                ("david.green", "David Green", "david.green@example.com"),
                ("grace.adams", "Grace Adams", "grace.adams@example.com"),
                ("joseph.baker", "Joseph Baker", "joseph.baker@example.com"),
                ("chloe.gonzalez", "Chloe Gonzalez", "chloe.gonzalez@example.com"),
                ("carter.nelson", "Carter Nelson", "carter.nelson@example.com"),
                ("victoria.carter", "Victoria Carter", "victoria.carter@example.com"),
                ("owen.mitchell", "Owen Mitchell", "owen.mitchell@example.com"),
                ("riley.perez", "Riley Perez", "riley.perez@example.com"),
                ("wyatt.roberts", "Wyatt Roberts", "wyatt.roberts@example.com"),
                ("aria.turner", "Aria Turner", "aria.turner@example.com"),
                ("john.phillips", "John Phillips", "john.phillips@example.com"),
                ("lily.campbell", "Lily Campbell", "lily.campbell@example.com"),
                ("jack.parker", "Jack Parker", "jack.parker@example.com"),
                ("aubrey.evans", "Aubrey Evans", "aubrey.evans@example.com"),
                ("luke.edwards", "Luke Edwards", "luke.edwards@example.com"),
                ("zoey.collins", "Zoey Collins", "zoey.collins@example.com"),
                ("jayden.stewart", "Jayden Stewart", "jayden.stewart@example.com"),
                ("penelope.sanchez", "Penelope Sanchez", "penelope.sanchez@example.com"),
                ("dylan.morris", "Dylan Morris", "dylan.morris@example.com"),
                ("layla.rogers", "Layla Rogers", "layla.rogers@example.com"),
                ("grayson.reed", "Grayson Reed", "grayson.reed@example.com"),
                ("nora.cook", "Nora Cook", "nora.cook@example.com"),
                ("levi.morgan", "Levi Morgan", "levi.morgan@example.com"),
                ("hazel.bell", "Hazel Bell", "hazel.bell@example.com"),
                ("isaac.murphy", "Isaac Murphy", "isaac.murphy@example.com"),
                ("aurora.bailey", "Aurora Bailey", "aurora.bailey@example.com"),
                ("gabriel.rivera", "Gabriel Rivera", "gabriel.rivera@example.com"),
                ("savannah.cooper", "Savannah Cooper", "savannah.cooper@example.com"),
                ("julian.richardson", "Julian Richardson", "julian.richardson@example.com"),
                ("brooklyn.cox", "Brooklyn Cox", "brooklyn.cox@example.com"),
                ("mateo.howard", "Mateo Howard", "mateo.howard@example.com"),
                ("bella.ward", "Bella Ward", "bella.ward@example.com"),
                ("anthony.torres", "Anthony Torres", "anthony.torres@example.com"),
                ("claire.peterson", "Claire Peterson", "claire.peterson@example.com"),
                ("jaxon.gray", "Jaxon Gray", "jaxon.gray@example.com"),
                ("skylar.ramirez", "Skylar Ramirez", "skylar.ramirez@example.com"),
                ("lincoln.james", "Lincoln James", "lincoln.james@example.com"),
                ("lucy.watson", "Lucy Watson", "lucy.watson@example.com"),
                ("joshua.brooks", "Joshua Brooks", "joshua.brooks@example.com"),
                ("paisley.kelly", "Paisley Kelly", "paisley.kelly@example.com"),
                ("christopher.sanders", "Christopher Sanders", "christopher.sanders@example.com"),
                ("everly.price", "Everly Price", "everly.price@example.com"),
                ("andrew.bennett", "Andrew Bennett", "andrew.bennett@example.com"),
                ("anna.wood", "Anna Wood", "anna.wood@example.com"),
                ("theodore.barnes", "Theodore Barnes", "theodore.barnes@example.com"),
                ("caroline.ross", "Caroline Ross", "caroline.ross@example.com"),
                ("caleb.henderson", "Caleb Henderson", "caleb.henderson@example.com"),
                ("nova.coleman", "Nova Coleman", "nova.coleman@example.com"),
                ("ryan.jenkins", "Ryan Jenkins", "ryan.jenkins@example.com"),
                ("genesis.perry", "Genesis Perry", "genesis.perry@example.com"),
                ("ashton.powell", "Ashton Powell", "ashton.powell@example.com"),
                ("emilia.long", "Emilia Long", "emilia.long@example.com"),
                ("nathan.patterson", "Nathan Patterson", "nathan.patterson@example.com"),
                ("kennedy.hughes", "Kennedy Hughes", "kennedy.hughes@example.com"),
                ("thomas.flores", "Thomas Flores", "thomas.flores@example.com"),
                ("maya.washington", "Maya Washington", "maya.washington@example.com"),
                ("leo.butler", "Leo Butler", "leo.butler@example.com"),
                ("willow.simmons", "Willow Simmons", "willow.simmons@example.com"),
                ("isaiah.foster", "Isaiah Foster", "isaiah.foster@example.com"),
                ("kinsley.gonzales", "Kinsley Gonzales", "kinsley.gonzales@example.com"),
                ("charles.bryant", "Charles Bryant", "charles.bryant@example.com"),
                ("naomi.alexander", "Naomi Alexander", "naomi.alexander@example.com"),
                ("josiah.russell", "Josiah Russell", "josiah.russell@example.com"),
                ("aaliyah.griffin", "Aaliyah Griffin", "aaliyah.griffin@example.com"),
                ("hudson.diaz", "Hudson Diaz", "hudson.diaz@example.com"),
                ("elena.hayes", "Elena Hayes", "elena.hayes@example.com")
            };
        }
    }
}
