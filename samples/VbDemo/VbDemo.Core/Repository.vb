Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Linq

''' <summary>A tiny generic repository, for the generic-inspection path.</summary>
Public Class Repository(Of T As Class)

    Private ReadOnly _items As New List(Of T)()

    Public Sub Add(item As T)
        If item Is Nothing Then Throw New ArgumentNullException(NameOf(item))
        _items.Add(item)
    End Sub

    Public ReadOnly Property Count As Integer
        Get
            Return _items.Count
        End Get
    End Property

    Public Function Find(match As Func(Of T, Boolean)) As IReadOnlyList(Of T)
        Return _items.Where(match).ToList()
    End Function

    Public Function All() As IReadOnlyList(Of T)
        Return _items.ToList()
    End Function

End Class
