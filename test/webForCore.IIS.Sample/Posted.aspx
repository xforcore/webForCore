<%@ Page Language="C#" %>
<%-- #214: what a POST or PUT sent, its Expect header, and the process that read it. --%>
<%
    string body;
    if (Request.HttpMethod == "POST") body = Request.Form["data"];
    else using (var reader = new System.IO.StreamReader(Request.InputStream)) body = reader.ReadToEnd();
%>method=<%= Request.HttpMethod %>;body=<%= body %>;expect=<%= Request.Headers["Expect"] %>;process=<%= System.Environment.ProcessId %>
