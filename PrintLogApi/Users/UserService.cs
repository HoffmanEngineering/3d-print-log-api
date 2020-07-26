using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Users
{
    public class UserService
    {
        private readonly PrintLogContext _context;

        public UserService(PrintLogContext context)
        {
            _context = context;
        }

        public User GetLocalUserByAuthUserId(string authUserId)
        {
            return _context.Users.Where(u => u.OAuthUserId == authUserId).FirstOrDefault();
        }

        public async Task<long> GetLocalUserIdByAuthUserId(string authUserId)
        {
            return await _context.Users.Where(u => u.OAuthUserId == authUserId).Select(u=> u.Id).FirstOrDefaultAsync();
        }

        public async Task<User> CreateUserFromAuthId(string authUserId)
        {
            var newUser = new User
            {
                OAuthUserId = authUserId,
                ViewStatus = User.ProfileViewStatus.Public
            };

            _context.Users.Add(newUser);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException dbUpdateEx) {
                // Check to see if there is a unique index exception thrown, 
                // due to a new user sending multiple HTTP requests at the same time and multiple local users trying to be created from the same auth id.
                if (dbUpdateEx.InnerException != null)
                {
                    if (dbUpdateEx.InnerException is SqlException sqlException)
                    {
                        switch (sqlException.Number)
                        {
                            case 2627:  // Unique constraint error
                            case 547:   // Constraint check violation
                            case 2601:  // Duplicated key row error
                                        // Constraint violation exception
                                        // A custom exception of yours for concurrency issues

                                // If we get a unique constraint error, then query for the user that was created.
                                return GetLocalUserByAuthUserId(authUserId);
                        }
                    }
                }
            }

            return newUser;
        }
    }
}
