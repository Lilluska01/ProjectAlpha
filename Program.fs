namespace TaskGenGiraffe

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Security.Claims
open Giraffe
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

module Program =

    type HistoryItem = { Id: Guid; Date: DateTime; Task: string; Status: string }
    let historyStore = ConcurrentDictionary<Guid, HistoryItem>()

    let getHistory: HttpHandler =
        fun next ctx ->
            let arr = historyStore.Values |> Seq.sortByDescending (fun x -> x.Date) |> Seq.toArray
            json arr next ctx

    let postHistory: HttpHandler =
        bindJson<{| task: string; status: string |}> (fun i ->
            fun next ctx ->
                let item = { Id = Guid.NewGuid(); Date = DateTime.UtcNow; Task = i.task; Status = i.status }
                historyStore.[item.Id] <- item
                setStatusCode 201 >=> json item <| next <| ctx
        )

    let deleteHistoryHandler (id: Guid): HttpHandler =
        fun next ctx ->
            match historyStore.TryRemove id with
            | true, _ -> setStatusCode 204 next ctx
            | _       -> RequestErrors.NOT_FOUND "History item not found" next ctx

    module Handlers =

        let htmlPage (bodyHtml: string) : HttpHandler =
            htmlString $"""
            <!DOCTYPE html>
            <html lang="hu">
            <head><meta charset="utf-8" /><title>Feladat Generátor – Auth</title></head>
            <body>{bodyHtml}</body>
            </html>
            """

        let users = Dictionary<string, string>()

        let registerPage: HttpHandler =
            htmlPage """
            <h1>Regisztráció</h1>
            <form method="post" action="/register">
              Felhasználónév:<br /><input name="username" /><br />
              Jelszó:<br /><input name="password" type="password" /><br />
              <button type="submit">Regisztrálok</button>
            </form>
            <p><a href="/login">Bejelentkezés</a></p>
            """

        let registerHandler: HttpHandler =
            fun next ctx -> task {
                let! form = ctx.Request.ReadFormAsync()
                let username = form.["username"].ToString().Trim()
                let password = form.["password"].ToString()
                if String.IsNullOrWhiteSpace username || String.IsNullOrWhiteSpace password then
                    return! htmlPage "<p>Minden mező kötelező!</p>" next ctx
                elif users.ContainsKey username then
                    return! htmlPage "<p>Már létező felhasználó!</p>" next ctx
                else
                    users.Add(username, password)
                    return! redirectTo false "/login" next ctx
            }

        let loginPage: HttpHandler =
            htmlPage """
            <h1>Bejelentkezés</h1>
            <form method="post" action="/login">
              Felhasználónév:<br /><input name="username" /><br />
              Jelszó:<br /><input name="password" type="password" /><br />
              <button type="submit">Bejelentkezés</button>
            </form>
            <p><a href="/register">Regisztráció</a></p>
            """

        let loginHandler: HttpHandler =
            fun next ctx -> task {
                let! form = ctx.Request.ReadFormAsync()
                let username = form.["username"].ToString().Trim()
                let password = form.["password"].ToString()
                match users.TryGetValue username with
                | true, pass when pass = password ->
                    let claims = [ Claim(ClaimTypes.Name, username) ]
                    let identity = ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)
                    let principal = ClaimsPrincipal(identity)
                    do! ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
                    return! redirectTo false "/index.html" next ctx
                | _ ->
                    return! htmlPage "<p>Hibás adatok!</p>" next ctx
            }

        let logoutHandler: HttpHandler =
            fun next ctx -> task {
                do! ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                return! redirectTo false "/login" next ctx
            }

    let webApp =
        choose [
            GET  >=> route "/register" >=> Handlers.registerPage
            POST >=> route "/register" >=> Handlers.registerHandler
            GET  >=> route "/login"    >=> Handlers.loginPage
            POST >=> route "/login"    >=> Handlers.loginHandler
            GET  >=> route "/logout"
                         >=> requiresAuthentication (challenge CookieAuthenticationDefaults.AuthenticationScheme)
                         >=> Handlers.logoutHandler

            requiresAuthentication (challenge CookieAuthenticationDefaults.AuthenticationScheme)
              >=>
              choose [
                GET    >=> route "/history"           >=> getHistory
                POST   >=> route "/history"           >=> postHistory
                DELETE >=> routef "/history/%O" deleteHistoryHandler
              ]

            GET >=> route "/" >=> requiresAuthentication (challenge CookieAuthenticationDefaults.AuthenticationScheme)
                                  >=> redirectTo false "/index.html"
            requiresAuthentication (challenge CookieAuthenticationDefaults.AuthenticationScheme)
              >=> htmlFile "wwwroot/index.html"

            setStatusCode 404 >=> text "404 – Az oldal nem található"
        ]

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)

        builder.Services.AddGiraffe() |> ignore
        builder.Services
               .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
               .AddCookie(fun opts ->
                    opts.LoginPath  <- PathString("/login")
                    opts.LogoutPath <- PathString("/logout")
               ) |> ignore
        builder.Services.AddAuthorization() |> ignore

        let app = builder.Build()

        app.UseAuthentication() |> ignore
        app.UseAuthorization()  |> ignore
        app.UseDefaultFiles()   |> ignore
        app.UseStaticFiles()    |> ignore
        app.UseGiraffe webApp

        printfn "Listening on http://localhost:5000"
        app.Run()
        0
