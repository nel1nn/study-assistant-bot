using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dialogue_without_borders_tg_bot
{
    public abstract class UserDB
    {
        public int Id { get; set; }
        public long TgId { get; set; }
        public string TgUsername { get; set; } = null!;
        public bool Isverified { get; set; } = false;
        public string? Name { get; set; }
        public string? Surname { get; set; }
        public string? Patronymic { get; set; }

        public string? State { get; set; }
        public int LastInlineMessageId { get; set; }
    }

    public class StudentDB : UserDB
    {
        public string? StudentCardFileId { get; set; }
        public long? StudentCardNumber { get; set; }
        public int Group { get; set; }
        public string Speciality { get; set; } = null!;
        public int Course { get; set; }
    }

    public class TeacherDB : UserDB
    {
        public string? Specialty { get; set; }  // предмет/специальность
        public string? Department { get; set; } // кафедра
       public string? NoteName { get; set; }
    }

    public class AdminDB : UserDB
    {
    }

    public class TeacherNote
    {
        public int Id { get; set; }
        public long TeacherTgId { get; set; } // какой учитель отправил
        public string Discipline { get; set; } = null!; // по какому предмету файл
        public string? FileId { get; set; } // айди файла на серверах тг
        public string? FileName { get; set; } // название файла
        public string? Text { get; set; } // поле для текстовых заметок
        public string NoteName { get; set; } = null!; //название заметки
        public DateTime CreatedTime { get; set; } // когда был загружен файл
        public List<StudyAnnouncement> Announcements { get; set; } = new();
    }

    public class StudyAnnouncement
    {
        public int Id { get; set; }

        // Внешний ключ на документ
        [ForeignKey(nameof(Document))]
        public int TeacherDocumentId { get; set; }
        public TeacherNote Document { get; set; } = null!;

        public string TargetGroup { get; set; } = "all"; // Номер группы или "all"
        public int TargetCourse {  get; set; } // номер курса
        public string Topic { get; set; } = null!;       // Тематика: "Пересдача", "Дедлайн", "Вопросы к экзамену"
        public string Content { get; set; } = null!;     // Четкий факт/информация
        public string? EventDate { get; set; }          // Дата события (если нейросеть её нашла, напр. "20.09")
    }
}
