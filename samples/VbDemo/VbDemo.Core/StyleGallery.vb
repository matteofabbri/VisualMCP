Option Strict Off
Option Explicit On

Imports System
Imports System.Collections.Generic

''' <summary>
''' Deliberately unidiomatic Visual Basic: every member is shaped so that one rule from the
''' Visual Basic column of Microsoft's code-style table has something to rewrite. The tests pin
''' the behaviour of each member, so simplify_vb_code can rewrite this file and the suite still
''' passes. Do not tidy this file by hand — it is the input of the demo, and the README lists the
''' id behind each shape.
''' <para>
''' This is the one file of the demo with Option Strict Off: the IDE0041 shape (x = Nothing on a
''' reference) is a compiler error under Option Strict On (BC32013, "use Is"), which is precisely
''' why the rule exists for legacy Visual Basic. The rest of the solution compiles strictly.
''' </para>
''' </summary>
Public Module StyleGallery

    Public Const DefaultTitle As String = "Untitled"

    Public Witness As Integer = 7

    ''' <summary>IDE0001: the Imports above already make the short name bind.</summary>
    Public Function ShortNames(titles As System.Collections.Generic.List(Of String)) As List(Of String)
        Dim trimmed As New List(Of String)()
        For Each title As String In titles
            trimmed.Add(title.Trim())
        Next
        Return trimmed
    End Function

    ''' <summary>IDE0002: Type.SharedMember where the shared member is reachable unqualified.</summary>
    Public Function SharedAccess() As Integer
        Return StyleGallery.Witness
    End Function

    ''' <summary>IDE0049: framework type names instead of the Visual Basic keywords.</summary>
    Public Function DaysInYear(year As System.Int32) As System.Int32
        If System.DateTime.IsLeapYear(year) Then
            Return 366
        End If
        Return 365
    End Function

    ''' <summary>IDE0041: a reference comparison against Nothing in legacy Visual Basic.</summary>
    Public Function IsMissing(value As Object) As Boolean
        Return value = Nothing
    End Function

    ''' <summary>IDE0029: the conditional null check is Visual Basic's binary If.</summary>
    Public Function OrElseDefault(book As Book) As Book
        Return If(book IsNot Nothing, book, New Book(0, DefaultTitle, "Unknown", 0))
    End Function

    ''' <summary>IDE0031: null propagation.</summary>
    Public Function TitleOrNothing(book As Book) As String
        Return If(book IsNot Nothing, book.Title, Nothing)
    End Function

    ''' <summary>IDE0030: a nullable value type checked through HasValue.</summary>
    Public Function OrZero(value As Integer?) As Integer?
        Return If(value.HasValue, value, 0)
    End Function

    ''' <summary>IDE0047: parentheses around a primary expression.</summary>
    Public Function Parenthesized(book As Book) As String
        Return (book.Title)
    End Function

    ''' <summary>IDE0054: an assignment that is a compound assignment.</summary>
    Public Function CountUp(limit As Integer) As Integer
        Dim total As Integer = 0
        For index As Integer = 1 To limit
            total = total + 1
        Next
        Return total
    End Function

    ''' <summary>IDE0100: a comparison with a boolean literal.</summary>
    Public Function IsAdult(age As Integer) As Boolean
        Dim adult As Boolean = age >= 18
        If adult = True Then
            Return True
        End If
        Return False
    End Function

    ''' <summary>IDE0075: a conditional that returns boolean literals.</summary>
    Public Function IsPositive(value As Integer) As Boolean
        Dim positive As Boolean = value > 0
        Return If(positive, True, False)
    End Function

    ''' <summary>IDE0084: Not (o Is other), with the repository's IsNot preference.</summary>
    Public Function IsDifferent(left As Book, right As Book) As Boolean
        Return Not (left Is right)
    End Function

    ''' <summary>IDE0081: ByVal is the default passing convention.</summary>
    Public Function Add(ByVal left As Integer, ByVal right As Integer) As Integer
        Return left + right
    End Function

    ''' <summary>IDE0004: a cast to the operand's own type.</summary>
    Public Function Identity(value As Integer) As Integer
        Return CType(value, Integer)
    End Function

End Module

''' <summary>IDE0003: Me.Member with nothing shadowing it.</summary>
Public Class Summary

    Private ReadOnly _text As String

    Public Sub New(text As String)
        Me._text = text
    End Sub

    Public Function Render() As String
        Return Me._text.ToUpperInvariant()
    End Function

End Class

''' <summary>A reference type with no equality overrides, so "= Nothing" is a pure reference test.</summary>
Public Class PlainThing

    Public Sub New(thingName As String)
        Name = thingName
    End Sub

    Public ReadOnly Property Name As String

End Class
