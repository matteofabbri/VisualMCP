Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks

''' <summary>
''' An in-memory library: small enough to read in one sitting and shaped to exercise the tools
''' (events, LINQ, an iterator, an async method, generics and a lock).
''' </summary>
Public Class LibraryService
    Implements ILibraryService

    Private ReadOnly _books As New List(Of Book)()
    Private ReadOnly _gate As New Object()

    Public Event BookAdded As EventHandler(Of BookAddedEventArgs) Implements ILibraryService.BookAdded

    Public ReadOnly Property Count As Integer Implements ILibraryService.Count
        Get
            SyncLock _gate
                Return _books.Count
            End SyncLock
        End Get
    End Property

    Public Sub Add(book As Book) Implements ILibraryService.Add
        If book Is Nothing Then Throw New ArgumentNullException(NameOf(book))

        SyncLock _gate
            _books.Add(book)
        End SyncLock

        RaiseEvent BookAdded(Me, New BookAddedEventArgs(book))
    End Sub

    Public Function FindByAuthor(author As String) As IReadOnlyList(Of Book) Implements ILibraryService.FindByAuthor
        SyncLock _gate
            Return _books _
                .Where(Function(candidate) String.Equals(candidate.Author, author, StringComparison.OrdinalIgnoreCase)) _
                .OrderBy(Function(candidate) candidate.Year) _
                .ToList()
        End SyncLock
    End Function

    Public Function FindByYear(year As Integer) As IReadOnlyList(Of Book) Implements ILibraryService.FindByYear
        SyncLock _gate
            Return _books _
                .Where(Function(candidate) candidate.Year = year) _
                .OrderBy(Function(candidate) candidate.Title) _
                .ToList()
        End SyncLock
    End Function

    Public Function Oldest() As Book Implements ILibraryService.Oldest
        SyncLock _gate
            Return _books.OrderBy(Function(candidate) candidate.Year).FirstOrDefault()
        End SyncLock
    End Function

    Public Iterator Function Titles() As IEnumerable(Of String) Implements ILibraryService.Titles
        Dim snapshot As List(Of Book)

        SyncLock _gate
            snapshot = New List(Of Book)(_books)
        End SyncLock

        For Each book In snapshot
            Yield book.Title
        Next
    End Function

    Public Async Function LoadAsync(books As IEnumerable(Of Book)) As Task Implements ILibraryService.LoadAsync
        If books Is Nothing Then Throw New ArgumentNullException(NameOf(books))

        For Each book In books
            Add(book)
            Await Task.Yield()
        Next
    End Function

End Class
