FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY FmCareerChallenge.Api.csproj ./
RUN dotnet restore FmCareerChallenge.Api.csproj
COPY . .
RUN dotnet publish FmCareerChallenge.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet"]
CMD ["FmCareerChallenge.Api.dll"]
