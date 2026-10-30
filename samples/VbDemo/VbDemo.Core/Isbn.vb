Option Strict On
Option Explicit On

Imports System

''' <summary>ISBN-10 helpers used by the demo and its tests.</summary>
Public Module Isbn

    Public Function Normalize(raw As String) As String
        If raw Is Nothing Then Return String.Empty
        Return raw.Replace("-", String.Empty).Replace(" ", String.Empty).ToUpperInvariant()
    End Function

    ''' <summary>The ISBN-10 checksum, or -1 when the input is not ten digits.</summary>
    Public Function Checksum(normalized As String) As Integer
        If normalized Is Nothing OrElse normalized.Length <> 10 Then Return -1

        Dim total As Integer = 0
        For index As Integer = 0 To normalized.Length - 1
            If Not Char.IsDigit(normalized(index)) Then Return -1

            Dim digit As Integer = Integer.Parse(normalized.Substring(index, 1))
            total += digit * (10 - index)
        Next

        Return total Mod 11
    End Function

    Public Function IsValid(raw As String) As Boolean
        Dim normalized As String = Normalize(raw)
        Return Checksum(normalized) = 0
    End Function

End Module
