Option Strict On
Option Explicit On

Imports System
Imports VbDemo.Core

Module Program

    Sub Main()
        Dim service As New LibraryService()
        service.Add(New Book(1, "The Pragmatic Programmer", "Hunt", 1999))
        service.Add(New Book(2, "Clean Code", "Martin", 2008))

        Console.WriteLine($"books: {service.Count}")

        For Each book As Book In service.FindByAuthor("Hunt")
            Console.WriteLine($"{book.Year}: {book.Title} ({book.Author})")
        Next

        Console.WriteLine($"leap 2024: {StyleGallery.DaysInYear(2024)}")

        Dim valid As Boolean = Isbn.IsValid("0-306-40615-2")
        Console.WriteLine($"isbn 0-306-40615-2: {valid}")
    End Sub

End Module
