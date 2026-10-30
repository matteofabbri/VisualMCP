Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports VbDemo.Core

<TestClass>
Public Class LibraryServiceTests

    <TestMethod>
    Public Sub Add_RaisesBookAdded()
        Dim service As New LibraryService()
        Dim raised As Integer = 0
        AddHandler service.BookAdded, Sub(sender, e) raised += 1

        service.Add(New Book(1, "The Pragmatic Programmer", "Hunt", 1999))

        Assert.AreEqual(1, raised)
        Assert.AreEqual(1, service.Count)
    End Sub

    <TestMethod>
    Public Sub Add_NullBook_Throws()
        Dim service As New LibraryService()

        Assert.ThrowsExactly(Of ArgumentNullException)(Sub() service.Add(Nothing))
    End Sub

    <TestMethod>
    Public Sub FindByAuthor_IsCaseInsensitive()
        Dim service = MakeService()

        Dim found = service.FindByAuthor("hunt")

        Assert.HasCount(1, found)
        Assert.AreEqual("The Pragmatic Programmer", found(0).Title)
    End Sub

    <TestMethod>
    Public Sub FindByYear_ReturnsOnlyThatYear()
        Dim service = MakeService()

        Dim found = service.FindByYear(2008)

        Assert.HasCount(1, found)
        Assert.AreEqual("Clean Code", found(0).Title)
    End Sub

    <TestMethod>
    Public Sub Oldest_ReturnsTheEarliestBook()
        Dim service = MakeService()

        Assert.AreEqual("The Pragmatic Programmer", service.Oldest().Title)
    End Sub

    <TestMethod>
    Public Sub Titles_YieldsEveryTitle()
        Dim service = MakeService()

        Dim titles = service.Titles().ToList()

        CollectionAssert.AreEquivalent(New List(Of String) From {"The Pragmatic Programmer", "Clean Code"}, titles)
    End Sub

    <TestMethod>
    Public Async Function LoadAsync_AddsEveryBook() As Task
        Dim service As New LibraryService()

        Await service.LoadAsync(New List(Of Book) From {
            New Book(1, "A", "One", 2001),
            New Book(2, "B", "Two", 2002)
        })

        Assert.AreEqual(2, service.Count)
    End Function

    <TestMethod>
    Public Sub Repository_FiltersWithAPredicate()
        Dim repository As New Repository(Of Book)()
        repository.Add(New Book(1, "A", "One", 2001))
        repository.Add(New Book(2, "B", "Two", 2002))

        Dim found = repository.Find(Function(book) book.Year = 2002)

        Assert.HasCount(1, found)
        Assert.AreEqual("B", found(0).Title)
    End Sub

    Private Shared Function MakeService() As LibraryService
        Dim service As New LibraryService()
        service.Add(New Book(1, "The Pragmatic Programmer", "Hunt", 1999))
        service.Add(New Book(2, "Clean Code", "Martin", 2008))
        Return service
    End Function

End Class
