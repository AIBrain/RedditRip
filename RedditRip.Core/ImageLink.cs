using RedditSharp.Things;
using System.Collections.Generic;
using System.Text;

namespace RedditRip.Core
{

    using System;

    public class ImageLink
    {
        public ImageLink(Post post, String url, String filename)
        {
            Post = post;
            Url = url;
            Filename = filename;
        }

        public Post Post { get; set; }

        public String Url { get; set; }

        public String Filename { get; set; }

        public String GetPostDetails(KeyValuePair<String, List<ImageLink>> post)
        {
            return GetPostDetails(this, post);
        }

        public static String GetPostDetails(ImageLink imageLink, KeyValuePair<String, List<ImageLink>> post)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Subreddit:\t{imageLink?.Post.SubredditName}");

            sb.Append($"User:\t\t{imageLink?.Post.AuthorName}");
            sb.Append(String.IsNullOrWhiteSpace(imageLink?.Post.AuthorFlairText)
                ? String.Empty
                : imageLink?.Post.AuthorFlairText);
            sb.AppendLine();

            if (imageLink?.Post?.Title != null)
            {
                sb.AppendLine(imageLink.Post.NSFW ? " - NSFW" : "");
                sb.AppendLine($"Post:\t\t{imageLink?.Post.Title}");
                sb.AppendLine($"Score:\t\t{imageLink?.Post.Score}");
                sb.AppendLine($"Link:\t\t{imageLink?.Post.Url}");

                sb.AppendLine();

                if (!String.IsNullOrWhiteSpace(imageLink?.Post.SelfText))
                {
                    sb.AppendLine();
                    sb.AppendLine($"Message:\t\t{imageLink?.Post.SelfText}");
                    sb.AppendLine();
                }

                sb.AppendLine($"Images:\t\t{post.Value.Count}");
                sb.AppendLine();
            }
            
            foreach (var link in post.Value)
            {
                sb.AppendLine(link.Url);
            }

            var details = sb.ToString();
            return details;
        }
    }
}