Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks

''' <summary>The contract the demo library implements.</summary>
Public Interface ILibraryService

    Event BookAdded As EventHandler(Of BookAddedEventArgs)

    ReadOnly Property Count As Integer

    Sub Add(book As Book)

    Function FindByAuthor(author As String) As IReadOnlyList(Of Book)

    Function FindByYear(year As Integer) As IReadOnlyList(Of Book)

    Function Oldest() As Book

    Function Titles() As IEnumerable(Of String)

    Function LoadAsync(books As IEnumerable(Of Book)) As Task

End Interface
