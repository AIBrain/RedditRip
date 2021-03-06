﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using RedditSharp;
using RedditSharp.Things;

namespace RedditRip.Core {

    public class RedditRip {
        private const Int32 PostQueryErrorLimit = 6;

        private readonly Boolean _allAuthorsPosts;

        private readonly Boolean _nsfw;

        private readonly Boolean _onlyNsfw;

        private readonly Boolean _verboseLogging;

        private String _filter;

        private Int32 _perSubThreadLimit = 5;

        public RedditRip( String filter, Boolean allAuthorsPosts, Boolean nsfw, Boolean onlyNsfw, Boolean verboseLogging ) {
            _verboseLogging = verboseLogging;
            _allAuthorsPosts = allAuthorsPosts;
            _onlyNsfw = onlyNsfw;
            _filter = filter;
            _nsfw = nsfw;
            Log = LogManager.GetLogger( Assembly.GetEntryAssembly().ManifestModule.Name );
        }

        public ILog Log {
            get; set;
        }

        public async Task DownloadPost( KeyValuePair<String, List<ImageLink>> post, String destination, CancellationToken token ) {
            if ( post.Value.Any() && !String.IsNullOrWhiteSpace( destination ) ) {
                var details = post.Value.FirstOrDefault().GetPostDetails( post );
                var subName = post.Value.FirstOrDefault().Post.SubredditName;
                var userName = post.Value.FirstOrDefault().Post.AuthorName;
                var postId = post.Key;
                var filePath = destination + Path.DirectorySeparatorChar + subName + Path.DirectorySeparatorChar + userName;
                var fileNameBase = $"{userName}_{subName}_{postId}";

                var dir = new DirectoryInfo( filePath );
                dir.Create();

                var existsingFiles = Directory.GetFiles( filePath, $"{fileNameBase}*", SearchOption.TopDirectoryOnly ).ToList();

                if ( existsingFiles.Count >= post.Value.Count )
                    return;

                var count = 0;
                foreach ( var imageLink in post.Value ) {
                    token.ThrowIfCancellationRequested();
                    var uri = new Uri( imageLink.Url );
                    var domain = uri.DnsSafeHost;
                    if ( String.IsNullOrWhiteSpace( domain ) || imageLink.Url.Contains( "removed.png" ) )
                        continue;

                    imageLink.Url = imageLink.Url.Replace( ".gifv", ".gif" );
                    var extention = GetExtention( imageLink.Url );

                    count++;
                    var fullFilePath = filePath + Path.DirectorySeparatorChar + fileNameBase + "_" + count.ToString( "0000" ) + extention;

                    if ( !existsingFiles.Contains( fullFilePath ) ) {
                        var retryCount = 0;
                        while ( !await SaveFile( imageLink, fullFilePath, extention ) ) {
                            retryCount++;
                            if ( retryCount >= 5 )
                                break;
                        }
                    }
                }

                var detailsFilename = filePath + Path.DirectorySeparatorChar + "postDetails.txt";
                File.WriteAllText( detailsFilename, details );
            }
        }

        public async Task<List<ImageLink>> GetImgurLinksFromSubReddit( Reddit reddit, String sub, SearchRange searchRange, Sorting sortOrder, String outputPath, CancellationToken token ) {
            token.ThrowIfCancellationRequested();
            Subreddit subreddit = null;
            var links = new List<ImageLink>();

            if ( !String.IsNullOrWhiteSpace( sub ) ) {
                try {
                    subreddit = reddit.GetSubreddit( sub );
                    OutputLine( $"{subreddit.Name}: Begining Search..." );
                }
                catch ( Exception e ) {
                    OutputLine( $"Error connecting to reddit: {e.Message}" );
                    return links;
                }
            }

            if ( _filter == null )
                _filter = String.Empty;

            var searchTo = DateTime.Now;
            var searchFrom = DateTime.Now;
            switch ( searchRange ) {
                case SearchRange.Today:
                    searchFrom = searchFrom.AddDays( -1 );
                    break;

                case SearchRange.Week:
                    searchFrom = searchFrom.AddDays( -7 );
                    break;

                case SearchRange.Fortnight:
                    searchFrom = searchFrom.AddDays( -14 );
                    break;

                case SearchRange.Month:
                    searchFrom = searchFrom.AddMonths( -1 );
                    break;

                case SearchRange.ThreeMonths:
                    searchFrom = searchFrom.AddMonths( -3 );
                    break;

                case SearchRange.SixMonths:
                    searchFrom = searchFrom.AddMonths( -6 );
                    break;
            }

            var search = !String.IsNullOrWhiteSpace( sub )
                ? searchRange == SearchRange.AllTime ? subreddit?.Search( _filter ) : subreddit?.Search( searchFrom, searchTo, sortOrder )
                : reddit.Search<Post>( _filter );

            token.ThrowIfCancellationRequested();
            var listings = search?.GetEnumerator();

            links = CombineLinkLists( await GetImagesFromListing( reddit, listings, outputPath, token ), links );

            return links;
        }

        private static String GetExtention( String imgurl ) {
            var extention = ( imgurl.Contains( '.' ) && imgurl.LastIndexOf( '.' ) > imgurl.LastIndexOf( '/' ) && imgurl.LastIndexOf( '.' ) < ( imgurl.Length - 2 ) ) ? imgurl.Substring( imgurl.LastIndexOf( '.' ) ) : String.Empty;

            if ( extention.Contains( '?' ) )
                extention = extention.Substring( 0, extention.IndexOf( '?' ) );
            return extention;
        }

        private static void SetImageProperty( Image image, Int32 propertyId, Byte[] value ) {
            var prop = ( PropertyItem )FormatterServices.GetUninitializedObject( typeof( PropertyItem ) );
            prop.Id = propertyId;
            prop.Value = value;
            prop.Len = prop.Value.Length;
            prop.Type = 1;
            image.SetPropertyItem( prop );
        }

        private List<ImageLink> CombineLinkLists( IEnumerable<ImageLink> results, List<ImageLink> links ) {
            foreach ( var link in results ) {
                if ( links.Exists( x => x.Url == link.Url ) )
                    OutputLine( $"Link already obtained: {link.Url} (XPost {link.Post.Url})", true );
                else
                    links.Add( link );
            }

            return links;
        }

        private async Task<String> GetHtml( String url, CancellationToken token ) {
            try {
                using ( var wchtml = new WebClient() ) {
                    return await wchtml.DownloadStringTaskAsync( new Uri( url ) );
                }
            }
            catch ( Exception e ) {
                OutputLine( $"\tError loading album {url}: {e.Message}", true );
                return String.Empty;
            }
        }

        private async Task<List<ImageLink>> GetImagesFromListing( Reddit reddit, IEnumerator<Post> listing, String outputPath, CancellationToken token ) {
            var erroCount = 0;
            var posts = new List<Post>();
            var links = new List<ImageLink>();
            var users = new HashSet<String>();
            var processedUsers = new HashSet<String>();
            try {
                while ( listing.MoveNext() ) {
                    token.ThrowIfCancellationRequested();
                    Console.WriteLine();
                    for ( var i = 0; i < _perSubThreadLimit; i++ ) {
                        if ( _allAuthorsPosts ) {
                            if ( !users.Contains( listing.Current.AuthorName ) && !processedUsers.Contains( listing.Current.AuthorName ) ) {
                                OutputLine( $"Adding user to batch: {listing.Current.AuthorName}", true );
                                users.Add( listing.Current.AuthorName );
                                i = users.Count - 1;
                            }
                        }
                        else {
                            if ( !listing.Current.Domain.Contains( "imgur.com" ) || ( !_nsfw && listing.Current.NSFW ) || ( _onlyNsfw && !listing.Current.NSFW ) ) {
                                var suffix = listing.Current.NSFW ? " NSFW" : listing.Current.Url.DnsSafeHost;
                                OutputLine( $"Skipping non-imgur link: {listing.Current.Url} ({suffix})", true );
                                continue;
                            }

                            posts.Add( listing.Current );
                        }

                        if ( !listing.MoveNext() )
                            break;
                    }

                    foreach ( var user in users ) {
                        token.ThrowIfCancellationRequested();
                        OutputLine( $"Getting all posts for user: {user}", true );
                        var userPosts = reddit.GetUser( user ).Posts.OrderByDescending( post => post.Score ).Where( post => post.Url.DnsSafeHost.Contains( "imgur.com" ) );

                        if ( _onlyNsfw ) {
                            userPosts = userPosts.Where( x => x.NSFW );
                        }

                        if ( !_nsfw ) {
                            userPosts = userPosts.Where( x => !x.NSFW );
                        }

                        foreach ( var userPost in userPosts ) {
                            token.ThrowIfCancellationRequested();
                            if ( posts.Exists( x => x.Url == userPost.Url ) || links.Exists( x => x.Url == userPost.Url.ToString() ) ) {
                                continue;
                            }

                            posts.Add( userPost );
                        }

                        processedUsers.Add( user );
                    }

                    users = new HashSet<String>();
                    OutputLine( $"Batch returned: {posts.Count} posts.", true );
                    var subName = String.Empty;
                    for ( var i = 0; i < posts.Count; i = i + _perSubThreadLimit ) {
                        var tasks = new List<Task>();

                        for ( var j = 0; j < _perSubThreadLimit; j++ ) {
                            if ( posts.Count <= ( i + j ) )
                                break;

                            var post = posts[ i + j ];
                            tasks.Add( GetImgurImageOrAlbumFromUrl( post, outputPath, token ) );
                        }
                        Task.WaitAll( tasks.ToArray() );

                        links = tasks.Cast<Task<List<ImageLink>>>().Aggregate( links, ( current, task ) => CombineLinkLists( task.Result, current ) );

                        if ( String.IsNullOrWhiteSpace( subName ) ) {
                            subName = links.First()?.Post.SubredditName;
                        }

                        OutputLine( $"Total Links found in {subName}: {links.Count}" );
                    }

                    posts = new List<Post>();
                }
            }
            catch ( IndexOutOfRangeException e ) {
                return links;
            }
            catch ( Exception e ) {
                if ( e is OperationCanceledException || e is AggregateException ) {
                    throw new OperationCanceledException();
                }

                OutputLine( $"Error: {e.Message}" );
                erroCount++;
                if ( erroCount > PostQueryErrorLimit ) {
                    throw;
                }
            }

            return links;
        }

        private async Task<List<ImageLink>> GetImgurImageOrAlbumFromUrl( Post post, String outputPath, CancellationToken token ) {
            token.ThrowIfCancellationRequested();
            var links = new List<ImageLink>();

            OutputLine( $"\tGetting links from post: {post.Title}", true );

            var url = new Uri( post.Url.ToString() ).GetLeftPart( UriPartial.Path ).Replace( ".gifv", ".gif" );

            url = url.StartsWith( "http://" ) ? url : "http://" + url.Substring( url.IndexOf( post.Url.DnsSafeHost, StringComparison.Ordinal ) );

            var name = Path.GetInvalidFileNameChars().Aggregate( post.AuthorName, ( current, c ) => current.Replace( c, '-' ) );

            var filepath = outputPath + "\\";

            if ( _allAuthorsPosts )
                filepath += post.AuthorName;
            else
                filepath += post.SubredditName + "\\" + name;

            var filename = filepath + $"\\{name}_{post.SubredditName}_{post.Id}";

            var extention = GetExtention( url );

            if ( !String.IsNullOrEmpty( extention ) ) {
                OutputLine( $"\tAdding Link {url}", true );
                links.Add( new ImageLink( post, url, filename ) );
                return links;
            }

            var htmlString = await GetHtml( url, token );
            if ( String.IsNullOrWhiteSpace( htmlString ) )
                return links;

            var caroselAlbum = htmlString.Contains( @"data-layout=""h""" );

            if ( caroselAlbum ) {
                htmlString = await GetHtml( url + "/all", token );
                if ( String.IsNullOrWhiteSpace( htmlString ) )
                    return links;
            }

            var gridAlbum = htmlString.Contains( @"data-layout=""g""" );

            if ( caroselAlbum && !gridAlbum )
                return links;

            var regPattern = new Regex( @"<img[^>]*?src\s*=\s*[""']?([^'"" >]+?)[ '""][^>]*?>", RegexOptions.IgnoreCase );

            var matchImageLinks = regPattern.Matches( htmlString );

            OutputLine( $"\tFound {matchImageLinks.Count} image(s) from link.", true );

            foreach ( Match m in matchImageLinks ) {
                token.ThrowIfCancellationRequested();
                var imgurl = m.Groups[ 1 ].Value.Replace( ".gifv", ".gif" );

                if ( !imgurl.Contains( "imgur.com" ) )
                    continue;

                if ( gridAlbum )
                    imgurl = imgurl.Remove( imgurl.LastIndexOf( '.' ) - 1, 1 );
                var domain = new Uri( imgurl ).DnsSafeHost;
                imgurl = imgurl.StartsWith( "http://" ) ? imgurl : "http://" + imgurl.Substring( imgurl.IndexOf( domain, StringComparison.Ordinal ) );

                links.Add( new ImageLink( post, imgurl, filename ) );
            }

            return links;
        }

        private void OutputLine( String message, Boolean verboseMessage = false ) {
            if ( verboseMessage && !_verboseLogging )
                return;

            Debug.WriteLine( $"{DateTime.Now.ToShortTimeString()}: {message}" );
            if ( verboseMessage ) {
                Log.Debug( message );
            }
            else {
                Log.Info( message );
            }
        }

        private async Task<Boolean> SaveFile( ImageLink imageLink, String filename, String extention ) {
            using ( var wc = new WebClient() ) {
                try {
                    var uri = new Uri( imageLink.Url );
                    var domain = uri.DnsSafeHost;

                    var link = imageLink.Url.StartsWith( "http://" ) ? imageLink.Url : "http://" + imageLink.Url.Substring( imageLink.Url.IndexOf( domain, StringComparison.Ordinal ) );

                    var tempFilename = Path.GetTempPath() + Path.GetRandomFileName() + extention;

                    await wc.DownloadFileTaskAsync( new Uri( link ), tempFilename );

                    using ( var stream = new FileStream( tempFilename, FileMode.Open, FileAccess.Read ) ) {
                        using ( var image = Image.FromStream( stream ) ) {

                            //XPTitle
                            SetImageProperty( image, 40091, Encoding.Unicode.GetBytes( imageLink.Post.Title + Char.MinValue ) );

                            //XPComment
                            SetImageProperty( image, 40092, Encoding.Unicode.GetBytes( link + Char.MinValue ) );

                            //XPAuthor
                            SetImageProperty( image, 40093, Encoding.Unicode.GetBytes( imageLink.Post.AuthorName + Char.MinValue ) );

                            //XPKeywords
                            SetImageProperty( image, 40094, Encoding.Unicode.GetBytes( imageLink.Post.SubredditName + ";" + imageLink.Post.AuthorName + ";" + imageLink.Post.AuthorFlairText + ";" + imageLink.Post.Domain + Char.MinValue ) );

                            //Save to desination
                            image.Save( filename );
                        }
                    }

                    //Delete temp file after web client has been disposed (makes sure no handles to file left over)
                    File.Delete( tempFilename );
                    OutputLine( $"Downloaded: {imageLink.Url} to {filename}", true );
                }
                catch ( OperationCanceledException ) {
                    throw;
                }
                catch ( Exception e ) {
                    OutputLine( $"Error: {imageLink.Url} to {filename}", true );
                    OutputLine( e.Message, true );
                    return false;
                }
                return true;
            }
        }
    }
}
