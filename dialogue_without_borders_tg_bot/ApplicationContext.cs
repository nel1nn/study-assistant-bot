using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace dialogue_without_borders_tg_bot
{
    internal class ApplicationContext : DbContext
    {
        public DbSet<UserDB> Users { get; set; } = null!;

        public DbSet<StudentDB> Students { get; set; } = null!;

        public DbSet<TeacherDB> Teachers { get; set; } = null!;

        public DbSet<TeacherNote> teacherNotes { get; set; } = null!;

        public DbSet<StudyAnnouncement> studyAnnouncements { get; set; } = null!;

        public ApplicationContext(DbContextOptions<ApplicationContext> options)
        : base(options)
        {
            Database.EnsureCreated();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);


            modelBuilder.Entity<StudyAnnouncement>()
                .HasOne(a => a.Document)         
                .WithMany(n => n.Announcements)
                .HasForeignKey(a => a.TeacherDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
